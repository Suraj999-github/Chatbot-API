using AiApi.Web.Configuration;
using AiApi.Web.Services.Jobs;
using AiApi.Web.Services.VectorStore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AiApi.Web.Controllers;

/// <summary>
/// Manages the document knowledge base.
/// Handles uploading, indexing, and removing PDF and Word documents.
///
/// All uploads are processed asynchronously in the background to avoid
/// memory pressure and keep the API responsive during large file processing.
/// After uploading, poll GET /api/documents/jobs/{jobId} to track progress.
/// </summary>
[ApiController]
[Route("api/documents")]
public class DocumentIngestionController(
    DocumentIndexingQueue queue,
    IJobStore jobStore,
    ILogger<DocumentIngestionController> logger) : ControllerBase
{
    private static readonly string[] AllowedExtensions = [".pdf", ".docx", ".doc"];

    // ── POST /api/documents/upload ─────────────────────────────────────────
    // Accepts a PDF or Word file, saves it to a temp location, and queues
    // it for background processing. Returns 202 Accepted immediately —
    // the client does not wait for embedding to finish.
    [HttpPost("upload")]
    [RequestSizeLimit(52_428_800)] // 50 MB max
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            logger.LogWarning("Upload rejected — no file provided");
            return BadRequest(new { error = "No file provided." });
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            logger.LogWarning(
                "Upload rejected — unsupported file type. FileName={File}, Extension={Ext}",
                file.FileName, ext);

            return BadRequest(new
            {
                error = $"'{ext}' not supported. Allowed: {string.Join(", ", AllowedExtensions)}"
            });
        }

        // Save the file to a temp path immediately so we can release
        // the HTTP connection and avoid holding the upload stream open
        // during the long embedding process
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{ext}");

        await using (var fs = System.IO.File.Create(tempPath))
            await file.CopyToAsync(fs, ct);

        logger.LogInformation(
            "File saved to temp storage. FileName={File}, Size={Size:N0} bytes, TempPath={Path}",
            file.FileName, file.Length, tempPath);

        // Create a job record so the client can track progress
        var job = jobStore.Create(file.FileName);
        var item = new IndexingWorkItem(
            JobId: job.JobId,
            TempFilePath: tempPath,
            FileName: file.FileName,
            Metadata: new()
            {
                ["uploaded_by"] = "api",
                ["upload_time"] = DateTime.UtcNow.ToString("O"),
            });

        // Push to the background processing channel
        await queue.EnqueueAsync(item);

        logger.LogInformation(
            "Indexing job queued. JobId={JobId}, FileName={File}, Size={Size:N0} bytes",
            job.JobId, file.FileName, file.Length);

        // 202 Accepted — work is queued but not yet done
        return Accepted(new
        {
            jobId = job.JobId,
            fileName = file.FileName,
            status = job.Status.ToString(),
            message = "File accepted. Poll /api/documents/jobs/{jobId} for progress.",
            pollUrl = $"/api/documents/jobs/{job.JobId}",
        });
    }

    // ── GET /api/documents/jobs/{jobId} ────────────────────────────────────
    // Returns the current state of a single indexing job.
    // Poll this after uploading to know when the document is ready to query.
    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJob(string jobId)
    {
        var job = jobStore.Get(jobId);

        if (job is null)
        {
            logger.LogWarning("Job not found. JobId={JobId}", jobId);
            return NotFound(new { error = $"Job '{jobId}' not found." });
        }

        logger.LogDebug(
            "Job status requested. JobId={JobId}, Status={Status}, Progress={Progress}%",
            job.JobId, job.Status, job.Progress);

        return Ok(new
        {
            job.JobId,
            job.FileName,
            status = job.Status.ToString(),
            job.Progress,
            job.TotalChunks,
            job.ChunksDone,
            job.DocumentId,
            job.Error,
            job.CreatedAt,
            job.CompletedAt,
        });
    }

    // ── GET /api/documents/jobs ────────────────────────────────────────────
    // Returns all indexing jobs ordered by creation time (newest first).
    // Useful for building an admin dashboard or monitoring uploads.
    [HttpGet("jobs")]
    public IActionResult GetAllJobs()
    {
        var jobs = jobStore.GetAll();

        logger.LogDebug("Returning all indexing jobs. Count={Count}", jobs.Count);

        return Ok(jobs.Select(job => new
        {
            job.JobId,
            job.FileName,
            status = job.Status.ToString(),
            job.Progress,
            job.TotalChunks,
            job.ChunksDone,
            job.Error,
            job.CreatedAt,
            job.CompletedAt,
        }));
    }

    // ── DELETE /api/documents/{documentId} ─────────────────────────────────
    // Removes a document and ALL of its chunks from the Qdrant vector store.
    // The documentId is the value returned in the job status once completed.
    // This does not affect other documents in the knowledge base.
    [HttpDelete("{documentId}")]
    public async Task<IActionResult> Remove(
        string documentId,
        [FromServices] IVectorStore vectorStore,
        [FromServices] IOptions<DocumentChatOptions> opts,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Removing document from knowledge base. DocumentId={DocumentId}",
            documentId);

        await vectorStore.DeleteDocumentChunksAsync(
            opts.Value.CollectionName, documentId, ct);

        logger.LogInformation(
            "Document removed successfully. DocumentId={DocumentId}",
            documentId);

        return Ok(new { message = $"Document '{documentId}' removed." });
    }

    // ── POST /api/documents/index-folder ──────────────────────────────────
    // Queues all supported documents found in a local folder for indexing.
    // Defaults to the /Documents folder at the project root.
    // Each file gets its own job — poll individually using the returned job IDs.
    [HttpPost("index-folder")]
    public async Task<IActionResult> IndexFolder(
        [FromQuery] string folderPath = "Documents")
    {
        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), folderPath);

        if (!Directory.Exists(fullPath))
        {
            logger.LogWarning(
                "Index folder not found. Path={Path}", fullPath);

            return NotFound(new { error = $"Folder '{fullPath}' not found." });
        }

        var files = Directory.GetFiles(fullPath)
            .Where(f => AllowedExtensions.Contains(
                Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        if (files.Count == 0)
        {
            logger.LogInformation(
                "No supported documents found in folder. Path={Path}", fullPath);

            return Ok(new { message = "No supported documents found." });
        }

        logger.LogInformation(
            "Queuing {Count} documents from folder. Path={Path}",
            files.Count, fullPath);

        var jobs = new List<object>();

        foreach (var file in files)
        {
            // Copy to temp so the original file stays unlocked while processing
            var ext = Path.GetExtension(file);
            var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{ext}");
            System.IO.File.Copy(file, tempPath, overwrite: true);

            var job = jobStore.Create(Path.GetFileName(file));
            var item = new IndexingWorkItem(
                JobId: job.JobId,
                TempFilePath: tempPath,
                FileName: Path.GetFileName(file),
                Metadata: new() { ["source"] = "folder-index" });

            await queue.EnqueueAsync(item);

            logger.LogInformation(
                "Folder document queued. JobId={JobId}, FileName={File}",
                job.JobId, job.FileName);

            jobs.Add(new
            {
                job.JobId,
                job.FileName,
                pollUrl = $"/api/documents/jobs/{job.JobId}"
            });
        }

        return Accepted(new
        {
            message = $"{files.Count} document(s) queued for indexing.",
            jobs,
        });
    }
}