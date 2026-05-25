namespace AiApi.Web.Services
{
    public class ConversationService(IAIChatService chatService)
    {
        private readonly List<ChatMessage> _history = [];

        public async Task<string> SendAsync(string userMessage)
        {
            _history.Add(new ChatMessage("user", userMessage));

            // Trim history to avoid context overflow (keep last N turns) 
            var contextWindow = _history.TakeLast(20).ToList();

            var response = await chatService.ChatAsync(contextWindow);
            _history.Add(new ChatMessage("assistant", response));

            return response;
        }

        public void AddSystemPrompt(string systemPrompt) =>
            _history.Insert(0, new ChatMessage("system", systemPrompt));

        public void ClearHistory() => _history.Clear();
    }
}
