using AiApi.Web.Models;

namespace AiApi.Web.Services.Jobs
{
    public interface IJobStore
    {
        IndexingJob Create(string fileName);
        IndexingJob? Get(string jobId);
        void Update(string jobId, Action<IndexingJob> mutate);
        IReadOnlyList<IndexingJob> GetAll();
    }
}
