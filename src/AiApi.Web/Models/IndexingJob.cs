namespace AiApi.Web.Models
{
    public class IndexingJob
    {
        public string JobId { get; init; } = Guid.NewGuid().ToString();
        public string FileName { get; set; } = string.Empty;
        public JobStatus Status { get; set; } = JobStatus.Queued;
        public string? DocumentId { get; set; }
        public int TotalChunks { get; set; }
        public int ChunksDone { get; set; }
        public string? Error { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }

        // Progress percentage 0-100
        public int Progress => Status switch
        {
            JobStatus.Queued => 0,
            JobStatus.Parsing => 10,
            JobStatus.Embedding => TotalChunks == 0 ? 20
                                   : 20 + (int)(60.0 * ChunksDone / TotalChunks),
            JobStatus.Storing => 85,
            JobStatus.Completed => 100,
            JobStatus.Failed => 0,
            _ => 0
        };
    }
    public enum JobStatus { Queued, Parsing, Embedding, Storing, Completed, Failed }
}
