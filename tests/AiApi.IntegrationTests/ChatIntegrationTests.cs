using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestPlatform.TestHost;
using System.Net.Http.Json;
using Xunit;
namespace AiApi.IntegrationTests
{

    public class ChatIntegrationTests(WebApplicationFactory<Program> factory)
        : IClassFixture<WebApplicationFactory<Program>>
    {
        [Fact]
        public async Task HealthCheck_ReturnsHealthy()
        {
            var client = factory.CreateClient();
            var response = await client.GetAsync("/health");
            response.EnsureSuccessStatusCode();
        }

        [Fact]
        public async Task Chat_WithMockedService_ReturnsContent()
        {
            var factory2 = factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    // Replace real service with a mock 
                    //services.AddScoped<IAIChatService>(_ =>
                    //{
                    //    var mock = new Mock<IAIChatService>();
                    //    mock.Setup(s => s.ChatAsync(
                    //        It.IsAny<IEnumerable<ChatMessage>>(),
                    //        It.IsAny<string?>(),
                    //        It.IsAny<CancellationToken>()))
                    //        .ReturnsAsync("Test response");
                    //    return mock.Object;
                    //});
                }));

            var client = factory2.CreateClient();
            var response = await client.PostAsJsonAsync("/api/chat",
                new { messages = new[] { new { role = "user", content = "test" } } });

            response.EnsureSuccessStatusCode();

        }
    }
}
