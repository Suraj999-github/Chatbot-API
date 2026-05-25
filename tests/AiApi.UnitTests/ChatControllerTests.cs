using AiApi.Web.Controllers;
using AiApi.Web.Models;
using AiApi.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
namespace AiApi.UnitTests
{

    public class ChatControllerTests
    {
        private readonly Mock<IAIChatService> _mockService = new();

        [Fact]
        public async Task Chat_ReturnsOk_WithValidRequest()
        {
            // Arrange 
            _mockService.Setup(s => s.ChatAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync("Hello! How can I help?");

            var controller = new ChatController(_mockService.Object,
                new Mock<ILogger<ChatController>>().Object);

            var request = new ChatRequest(
                [new MessageDto("user", "Hi")], Model: "llama3");

            // Act 
            var result = await controller.Chat(request, default);

            // Assert 
            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<ChatResponse>(ok.Value);
            Assert.Contains("Hello", response.Content);
        }

        [Fact]
        public async Task Chat_ReturnsBadRequest_WhenNoMessages()
        {
            var controller = new ChatController(_mockService.Object,
                new Mock<ILogger<ChatController>>().Object);
            var request = new ChatRequest([], Model: null);

            var result = await controller.Chat(request, default);

            Assert.IsType<BadRequestObjectResult>(result);
        }
    }
}
