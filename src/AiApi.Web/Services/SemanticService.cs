using Microsoft.SemanticKernel;

namespace AiApi.Web.Services
{
    public class SemanticService(Kernel kernel)
    {
        public async Task<string> SummarizeAsync(string text)
        {
            var fn = kernel.CreateFunctionFromPrompt(
                "Summarize this in 3 bullet points: {{$input}}");
            var result = await kernel.InvokeAsync(fn, new() { ["input"] = text });
            return result.GetValue<string>() ?? "";
        }
    }
}
