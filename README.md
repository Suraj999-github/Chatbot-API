# Document-Grounded AI Chatbot

> An AI-powered customer support chatbot built on ASP.NET Core 8 that answers questions **exclusively from your company documents**. No hallucinations. No external knowledge. Just your data, served intelligently.

Built with **Ollama** (local LLM — no API keys), **Qdrant** (vector search), and a clean RAG pipeline that runs entirely on your machine.

---

## What This Does

You upload your company PDFs and Word documents. The chatbot reads them, understands them, and answers customer questions based only on what's in those documents.

If a customer asks something that isn't covered in your documents, the bot responds with a polite fallback — *"Please contact our support team for further assistance."* — instead of making something up.

```
Customer: "What is your return policy for digital products?"
Bot:      "Digital products are non-refundable within 48 hours of purchase,
           except in cases where the file is corrupted or inaccessible..."
           [sourced from: return-policy.pdf]

Customer: "What is the weather in London?"
Bot:      "I don't have enough information in my knowledge base to answer
           that question accurately. Please contact our support team."
```

---

## How It Works

```
Your Documents (PDF / Word)
        │
        ▼
   Parse Text  ──►  Split into Chunks  ──►  Embed with nomic-embed-text
                                                      │
                                                      ▼
                                               Store in Qdrant (vector DB)

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Customer Question
        │
        ▼
   Embed Question  ──►  Search Qdrant  ──►  Retrieve Top 5 Chunks
                                                      │
                                                      ▼
                                        Build Prompt with Context
                                                      │
                                                      ▼
                                           llama3 Generates Answer
                                                      │
                                                      ▼
                                            Stream to Customer
```

Document uploads are processed in the **background** — the API returns immediately and you poll for progress. This means uploads never block your server or crash it on large files.

---

## Tech Stack

| Tool | What It Does | Why We Use It |
|------|-------------|---------------|
| **ASP.NET Core 8** | Web API framework | Fast, modern, production-ready |
| **Ollama** | Runs LLMs locally | No API key, no cost, full privacy |
| **llama3** | Chat / answer generation | Meta's open model, excellent quality |
| **nomic-embed-text** | Converts text to vectors | Purpose-built for search, tiny (274MB) |
| **Qdrant** | Vector database | Stores and searches document embeddings |
| **PdfPig** | PDF text extraction | Open-source, no Adobe dependency |
| **OpenXml SDK** | Word doc extraction | Official Microsoft library |
| **Serilog** | Structured logging | Human-readable, file + console output |
| **Docker** | Container orchestration | One command to run everything |

---

## Prerequisites

Before you start, you need these installed:

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [Ollama](https://ollama.com/download)

---

## Getting Started

### 1. Clone the repo

```bash
git clone https://github.com/your-username/AiApi.git
cd AiApi
```

### 2. Pull the AI models

```bash
# The chat model — answers customer questions (~4.7 GB)
ollama pull llama3

# The embedding model — converts text to vectors (~274 MB)
ollama pull nomic-embed-text

# Verify both downloaded
ollama list
```

### 3. Start Qdrant (vector database)

```bash
docker run -d --name qdrant \
  -p 6333:6333 \
  -p 6334:6334 \
  -v qdrant_storage:/qdrant/storage \
  qdrant/qdrant
```

### 4. Start Ollama

```bash
ollama serve
```

### 5. Run the API

```bash
dotnet run --project src/AiApi.Web
```

The API will be live at `https://localhost:7155` and Swagger UI at `https://localhost:7155/swagger`.

---

## Or: Run Everything with Docker Compose

One command starts the API, Ollama, and Qdrant together:

```bash
docker compose up -d
```

First run will pull the models automatically. Give it a few minutes.

---

## Project Structure

```
AiApi/
├── src/
│   └── AiApi.Web/
│       ├── Controllers/
│       │   ├── DocumentIngestionController.cs   # Upload & manage documents
│       │   ├── DocumentChatController.cs        # Ask questions
│       │   └── ChatController.cs                # General chat (non-RAG)
│       ├── Services/
│       │   ├── Documents/      # PDF and Word parsing
│       │   ├── Embeddings/     # Text → vector conversion
│       │   ├── VectorStore/    # Qdrant integration
│       │   ├── Ingestion/      # Chunk + embed + store pipeline
│       │   ├── Chat/           # RAG query pipeline
│       │   └── Jobs/           # Background processing
│       ├── Configuration/      # Strongly-typed settings
│       ├── Models/             # Request / response models
│       ├── Middleware/         # Error handling
│       └── Documents/          # Drop your company docs here
└── tests/
    ├── AiApi.UnitTests/
    └── AiApi.IntegrationTests/
```

---

## Configuration

Everything is controlled from `appsettings.json`. You rarely need to touch the code.

```json
{
  "AiProvider": {
    "Provider": "Ollama",
    "BaseUrl": "http://localhost:11434/v1/",
    "DefaultModel": "llama3",
    "EmbeddingModel": "nomic-embed-text",
    "TimeoutSeconds": 300
  },
  "DocumentChat": {
    "FallbackMessage": "I don't have enough information in my knowledge base to answer that question accurately. Please contact our support team for further assistance.",
    "MinRelevanceScore": 0.65,
    "MaxChunksToRetrieve": 5,
    "MaxChunkSize": 1500,
    "ChunkOverlap": 200,
    "CollectionName": "company_documents"
  },
  "Qdrant": {
    "Host": "localhost",
    "Port": 6334
  }
}
```

**Key settings explained:**

| Setting | Default | What It Does |
|---------|---------|-------------|
| `DefaultModel` | `llama3` | LLM used to generate answers |
| `EmbeddingModel` | `nomic-embed-text` | Model used to embed text for search |
| `MinRelevanceScore` | `0.65` | How closely a chunk must match the question (0–1). Raise this to be stricter |
| `MaxChunksToRetrieve` | `5` | How many document chunks to send to the LLM as context |
| `MaxChunkSize` | `1500` | Characters per chunk when splitting documents |
| `FallbackMessage` | see above | What the bot says when it can't find an answer |

---

## API Reference

### Document Management

#### Upload a Document

Upload a PDF or Word file. Returns immediately with a job ID — processing happens in the background.

```
POST /api/documents/upload
Content-Type: multipart/form-data
```

```bash
curl -X POST https://localhost:7155/api/documents/upload \
  -F "file=@./company-policy.pdf"
```

**Response `202 Accepted`:**
```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fileName": "company-policy.pdf",
  "status": "Queued",
  "message": "File accepted. Poll /api/documents/jobs/{jobId} for progress.",
  "pollUrl": "/api/documents/jobs/3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

---

#### Check Upload Progress

Poll this endpoint after uploading to track indexing progress.

```
GET /api/documents/jobs/{jobId}
```

```bash
curl https://localhost:7155/api/documents/jobs/3fa85f64-5717-4562-b3fc-2c963f66afa6
```

**Response — while processing:**
```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fileName": "company-policy.pdf",
  "status": "Embedding",
  "progress": 45,
  "totalChunks": 62,
  "chunksDone": 28,
  "documentId": null,
  "error": null,
  "createdAt": "2025-01-15T10:30:00Z",
  "completedAt": null
}
```

**Response — when done:**
```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fileName": "company-policy.pdf",
  "status": "Completed",
  "progress": 100,
  "totalChunks": 62,
  "chunksDone": 62,
  "documentId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "error": null,
  "createdAt": "2025-01-15T10:30:00Z",
  "completedAt": "2025-01-15T10:33:42Z"
}
```

**Job status values:**

| Status | Meaning |
|--------|---------|
| `Queued` | Waiting to start |
| `Parsing` | Reading the PDF or Word file |
| `Embedding` | Converting chunks to vectors |
| `Storing` | Saving to Qdrant |
| `Completed` | Ready to answer questions |
| `Failed` | Something went wrong — check `error` field |

---

#### List All Jobs

```
GET /api/documents/jobs
```

```bash
curl https://localhost:7155/api/documents/jobs
```

**Response:**
```json
[
  {
    "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "fileName": "company-policy.pdf",
    "status": "Completed",
    "progress": 100,
    "totalChunks": 62,
    "chunksDone": 62,
    "createdAt": "2025-01-15T10:30:00Z",
    "completedAt": "2025-01-15T10:33:42Z"
  },
  {
    "jobId": "9cb71234-1234-5678-def0-123456789abc",
    "fileName": "product-catalog.docx",
    "status": "Embedding",
    "progress": 60,
    "totalChunks": 40,
    "chunksDone": 24,
    "createdAt": "2025-01-15T10:35:00Z",
    "completedAt": null
  }
]
```

---

#### Index All Documents in a Folder

Queues all PDFs and Word files from the `/Documents` folder at once.

```
POST /api/documents/index-folder?folderPath=Documents
```

```bash
curl -X POST "https://localhost:7155/api/documents/index-folder?folderPath=Documents"
```

**Response `202 Accepted`:**
```json
{
  "message": "3 document(s) queued for indexing.",
  "jobs": [
    {
      "jobId": "aaa-111",
      "fileName": "policies.pdf",
      "pollUrl": "/api/documents/jobs/aaa-111"
    },
    {
      "jobId": "bbb-222",
      "fileName": "faq.pdf",
      "pollUrl": "/api/documents/jobs/bbb-222"
    },
    {
      "jobId": "ccc-333",
      "fileName": "manual.docx",
      "pollUrl": "/api/documents/jobs/ccc-333"
    }
  ]
}
```

---

#### Remove a Document

Remove a document and all its chunks from the knowledge base.

```
DELETE /api/documents/{documentId}
```

```bash
curl -X DELETE https://localhost:7155/api/documents/a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

**Response:**
```json
{
  "message": "Document 'a1b2c3d4-e5f6-7890-abcd-ef1234567890' removed."
}
```

---

### Chatbot

#### Ask a Question (standard)

Ask a question and wait for the full answer.

```
POST /api/document-chat/ask
Content-Type: application/json
```

```bash
curl -X POST https://localhost:7155/api/document-chat/ask \
  -H "Content-Type: application/json" \
  -d '{"question": "What documents do I need to submit a warranty claim?"}'
```

**Response — answer found in documents:**
```json
{
  "answer": "To submit a warranty claim, you will need to provide the following documents:\n\n1. Original proof of purchase (receipt or invoice)\n2. A completed warranty claim form (available on our website)\n3. Photos of the defective product\n4. The product serial number\n\nPlease submit these to support@company.com or visit any of our service centres.",
  "isGrounded": true,
  "sources": ["warranty-policy.pdf"],
  "chunksUsed": 3
}
```

**Response — question is outside document scope:**
```json
{
  "answer": "I don't have enough information in my knowledge base to answer that question accurately. Please contact our support team for further assistance.",
  "isGrounded": false,
  "sources": [],
  "chunksUsed": 0
}
```

---

#### Ask a Question (streaming)

Get the answer word-by-word as it's generated. Great for chat UIs.

```
POST /api/document-chat/ask/stream
Content-Type: application/json
```

```bash
curl -X POST https://localhost:7155/api/document-chat/ask/stream \
  -H "Content-Type: application/json" \
  -d '{"question": "Summarize the refund policy"}' \
  --no-buffer
```

**Response — Server-Sent Events stream:**
```
data: To

data:  request

data:  a

data:  refund

data: ,

data:  please

data:  contact

data:  us

data:  within

data:  30

data:  days

data: ...

data: [DONE]
```

---

#### Ask with Session (multi-turn conversation)

Pass a `sessionId` to maintain conversation history across multiple questions.

```bash
# First message
curl -X POST https://localhost:7155/api/document-chat/ask \
  -H "Content-Type: application/json" \
  -d '{
    "question": "What is the return window for electronics?",
    "sessionId": "customer-session-abc123"
  }'

# Follow-up — model remembers the context
curl -X POST https://localhost:7155/api/document-chat/ask \
  -H "Content-Type: application/json" \
  -d '{
    "question": "What if the item is damaged?",
    "sessionId": "customer-session-abc123"
  }'
```

---

### General Chat (non-RAG)

Direct chat with the LLM without document context. Useful for testing the model.

#### Standard chat

```
POST /api/chat
Content-Type: application/json
```

```bash
curl -X POST https://localhost:7155/api/chat \
  -H "Content-Type: application/json" \
  -d '{
    "model": "llama3",
    "messages": [
      { "role": "user", "content": "Hello, how are you?" }
    ]
  }'
```

**Response:**
```json
{
  "content": "Hello! I'm doing well, thank you for asking. How can I help you today?",
  "model": "llama3"
}
```

#### Streaming chat

```bash
curl -X POST https://localhost:7155/api/chat/stream \
  -H "Content-Type: application/json" \
  -d '{
    "messages": [{ "role": "user", "content": "Explain what RAG means in AI" }]
  }' \
  --no-buffer
```

---

### Health Check

```
GET /health
```

```bash
curl https://localhost:7155/health
```

**Response:**
```json
{
  "status": "Healthy",
  "results": {
    "ollama": {
      "status": "Healthy",
      "description": "Ollama is responding"
    }
  }
}
```

---

## Consuming the Stream from JavaScript

```javascript
async function askQuestion(question) {
  const response = await fetch('/api/document-chat/ask/stream', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ question })
  });

  const reader  = response.body.getReader();
  const decoder = new TextDecoder();

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;

    const lines = decoder.decode(value).split('\n');
    for (const line of lines) {
      if (!line.startsWith('data: ')) continue;
      const token = line.replace('data: ', '');
      if (token === '[DONE]') return;
      appendToChat(token); // your UI update function
    }
  }
}
```

---

## About the Models

### llama3

Meta's Llama 3 is the model that reads your document context and writes the answers.

- **Size:** ~4.7 GB on disk
- **RAM needed:** ~6 GB minimum, 8 GB recommended
- **Speed:** 5–15 tokens/sec on CPU, 40–80 tokens/sec on GPU
- **Why llama3:** Strong reasoning, follows instructions reliably, good at staying within given context

```bash
# Pull
ollama pull llama3

# Test it directly
ollama run llama3 "Explain what a vector database is in simple terms"
```

### nomic-embed-text

This is the embedding model — it converts text into numbers (vectors) that represent meaning. Qdrant stores these numbers and finds the most similar ones when a question is asked.

- **Size:** ~274 MB on disk
- **RAM needed:** ~1 GB
- **Output:** 768-dimensional vectors
- **Speed:** Very fast — typically under 1 second per chunk
- **Why nomic-embed-text:** Purpose-built for document search and retrieval. Much faster and smaller than using a chat model for embeddings

```bash
ollama pull nomic-embed-text
```

### Switching to a Different Model

You can switch the chat model without touching any code. Just update `appsettings.json`:

```json
"AiProvider": {
  "DefaultModel": "mistral"
}
```

Then pull the new model:

```bash
ollama pull mistral
```

Other good options:

| Model | Size | Best For |
|-------|------|---------|
| `llama3` | 4.7 GB | Best quality (default) |
| `mistral` | 4.1 GB | Faster responses |
| `phi3` | 2.3 GB | Low-memory machines |
| `gemma:2b` | 1.7 GB | Minimal hardware |

---

## About Qdrant

Qdrant is the vector database that powers the document search. It stores the embedded representations of your document chunks and finds the most relevant ones for each customer question using cosine similarity.

**Why Qdrant:**
- Runs locally via Docker — your data never leaves your machine
- Fast even with millions of vectors
- Supports filtering (e.g. search only within a specific document)
- Has a clean REST and gRPC API

**Useful Qdrant endpoints (for debugging):**

```bash
# View all collections
curl http://localhost:6333/collections

# View your document collection
curl http://localhost:6333/collections/company_documents

# Count how many chunks are stored
curl http://localhost:6333/collections/company_documents/points/count

# Open the Qdrant web dashboard
open http://localhost:6333/dashboard
```

---

## Supported File Types

| Format | Extension | Notes |
|--------|-----------|-------|
| PDF | `.pdf` | Text-based PDFs only. Scanned/image PDFs need OCR pre-processing |
| Word | `.docx` | Full support including tables |
| Word (legacy) | `.doc` | Supported via conversion |

**If your PDF is scanned and text extraction fails:**

Open the PDF in Microsoft Edge → Press `Ctrl+P` → Print to **Microsoft Print to PDF**. Edge re-renders the pages and the resulting file usually has a selectable text layer. Alternatively use a free OCR tool like [PDF24](https://tools.pdf24.org/en/ocr-pdf).

---

## Troubleshooting

**Ollama not responding**
```bash
# Check if Ollama is running
curl http://localhost:11434/api/version

# Start it
ollama serve
```

**Qdrant not reachable**
```bash
# Check if container is running
docker ps | grep qdrant

# Start it
docker start qdrant

# Or fresh start
docker run -d --name qdrant -p 6333:6333 -p 6334:6334 qdrant/qdrant
```

**Upload is slow**
This is normal for large documents. A 1MB PDF with 80 chunks takes 2–5 minutes on CPU because each chunk must be embedded by Ollama. Uploads run in the background — poll `/api/documents/jobs/{jobId}` to track progress. Your API stays responsive throughout.

**Model takes too long to respond**
First request after a cold start is always slow — llama3 loads from disk into RAM (~10–30 seconds). Subsequent requests are much faster. Set `TimeoutSeconds` to at least `300` in config.

**Out of memory errors**
Switch to a smaller model. In `appsettings.json` set `"DefaultModel": "phi3"` and run `ollama pull phi3`. Also ensure Qdrant data is persisted to a Docker volume so you don't re-index everything after a restart.

**PDF extraction returns no text**
The PDF is likely scanned (image-based). Use an OCR tool to make it searchable before uploading. See the Supported File Types section above.

---

## Migrating to OpenAI or Azure OpenAI

The entire system is provider-agnostic. When you're ready to move to a cloud model, change only your config:

**OpenAI:**
```json
"AiProvider": {
  "Provider": "OpenAI",
  "BaseUrl": "https://api.openai.com/v1/",
  "ApiKey": "sk-your-key-here",
  "DefaultModel": "gpt-4o-mini"
}
```

**Azure OpenAI:**
```json
"AiProvider": {
  "Provider": "AzureOpenAI",
  "BaseUrl": "https://YOUR-RESOURCE.openai.azure.com/openai/deployments/YOUR-DEPLOYMENT/v1/",
  "ApiKey": "your-azure-key",
  "DefaultModel": "gpt-4o"
}
```

No code changes required. Store API keys in environment variables or .NET User Secrets — never commit them to git.

```bash
dotnet user-secrets set "AiProvider:ApiKey" "sk-..." --project src/AiApi.Web
```

---

## Contributing

Pull requests are welcome. For major changes, open an issue first to discuss what you'd like to change.

1. Fork the repo
2. Create your branch: `git checkout -b feature/your-feature`
3. Commit your changes: `git commit -m 'Add some feature'`
4. Push to the branch: `git push origin feature/your-feature`
5. Open a pull request

---

*Built with using .NET 8, Ollama, and Qdrant. Runs entirely on your machine — your documents stay private.*
