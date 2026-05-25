using AiApi.Web.Models;
using System.Collections.Concurrent;

namespace AiApi.Web.Services.Jobs
{
    // Singleton — holds job state for the lifetime of the app.
    // For multi-server: replace with Redis or SQL.
    public sealed class InMemoryJobStore : IJobStore
    {
        private readonly ConcurrentDictionary<string, IndexingJob> _jobs = new();

        public IndexingJob Create(string fileName)
        {
            var job = new IndexingJob { FileName = fileName };
            _jobs[job.JobId] = job;
            return job;
        }

        public IndexingJob? Get(string jobId) =>
            _jobs.TryGetValue(jobId, out var job) ? job : null;

        public void Update(string jobId, Action<IndexingJob> mutate)
        {
            if (_jobs.TryGetValue(jobId, out var job))
                mutate(job);
        }

        public IReadOnlyList<IndexingJob> GetAll() =>
            _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();
    }
}
