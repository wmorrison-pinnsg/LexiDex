# LexiDex

Local semantic search for your files. No API keys, no cloud services, no telemetry — just fast meaning-based search running entirely on your machine.

LexiDex indexes text, markdown, PDF, and source code files into a local vector database using [BGE-small-en-v1.5](https://huggingface.co/BAAI/bge-small-en-v1.5) embeddings via ONNX Runtime, then lets you search by meaning rather than keywords.

## Features

- **Fully offline** — runs locally, no network calls
- **Broad file support** — `.txt`, `.md`, `.pdf`, `.cs`, `.py`, `.ts`, `.js`, `.java`, `.go`, `.rs`, `.cpp`, `.json`, `.yaml`, and more
- **Smart chunking** — paragraph-aware splitting with configurable size and overlap
- **Code-aware** — detects classes, functions, and methods for structure-based chunking; optional comment stripping
- **Incremental indexing** — only re-indexes changed files on subsequent runs
- **Interactive search** — drill into results, re-search without restarting

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

## Setup

```bash
git clone https://github.com/wmorrison-pinnsg/LexiDex.git
cd LexiDex/LexiDex
```

The ONNX model (~416MB) is not included in the repo. Download it and place it at `LexiDex/Models/model.onnx`.

You can get it from HuggingFace:

```bash
# Using huggingface-cli
pip install huggingface_hub
huggingface-cli download BAAI/bge-small-en-v1.5 model.onnx --local-dir LexiDex/Models

# Or download directly
curl -L -o LexiDex/Models/model.onnx https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/main/model.onnx
```

The tokenizer files (`tokenizer.json`, `tokenizer_config.json`, `config.json`) are included in the repo.

## Usage

### Index a directory

```bash
dotnet run -- index /path/to/documents
```

### Search

```bash
dotnet run -- search /path/to/documents "how does authentication work"
```

If no index exists, it will be built automatically before searching.

### CLI Options

**`index`**

| Flag | Default | Description |
|------|---------|-------------|
| `--chunk-size` | 500 | Characters per chunk |
| `--overlap` | 50 | Overlap between chunks |
| `--extensions` | built-in list | Comma-separated file extensions to index |
| `--include-comments` | off | Preserve comments in code files |
| `--force` | off | Re-index all files from scratch |

**`search`**

| Flag | Default | Description |
|------|---------|-------------|
| `--top-k` | 5 | Number of results to return |

### Examples

```bash
# Index only Python files with smaller chunks
dotnet run -- index ./src --extensions ".py" --chunk-size 300 --overlap 30

# Search a codebase, return top 10 results
dotnet run -- search ./src "database connection pooling" --top-k 10

# Force a full re-index after changing chunk settings
dotnet run -- index ./docs --chunk-size 800 --force
```

## How It Works

1. **File extraction** — Text is extracted per file type (raw read for `.txt`, Markdig for `.md`, PdfPig for `.pdf`, custom extractors for code files)
2. **Chunking** — Documents are split into segments respecting paragraph boundaries, then assembled into chunks with configurable overlap
3. **Embedding** — Each chunk is tokenized with a BERT WordPiece tokenizer and run through the BGE ONNX model to produce a 768-dimensional vector, L2-normalized for cosine similarity
4. **Storage** — Embeddings and metadata are stored in SQLite with the [sqlite-vec](https://github.com/asg017/sqlite-vec) extension
5. **Search** — Queries are embedded the same way and matched against the vector store using Euclidean distance, returning the top-K most similar chunks

## Architecture

```
Program.cs              CLI entry point (System.CommandLine + Spectre.Console)
SearchEngine.cs         Indexing orchestration, vector store, incremental tracking
FileIndexer.cs          File scanning, text extraction, chunking
CodeExtractor.cs        Code file comment stripping and structure detection
BgeEmbeddingService.cs  ONNX Runtime inference, mean pooling, L2 normalization
Tokenizer.cs            BERT WordPiece tokenizer (HuggingFace format)
FileTracker.cs          File metadata tracking for incremental indexing
DocumentChunk.cs        Vector store record model
IndexOptions.cs         Index configuration POCO
SearchOptions.cs        Search configuration POCO
```

## Dependencies

| Package | Purpose |
|---------|---------|
| Microsoft.ML.OnnxRuntime | Local ONNX model inference |
| Microsoft.SemanticKernel.Connectors.Sqlite | SQLite vector store |
| PdfPig | PDF text extraction |
| Markdig | Markdown to plain text |
| System.CommandLine | CLI argument parsing |
| Spectre.Console | Terminal UI (progress bars, tables, panels) |

## License

MIT
