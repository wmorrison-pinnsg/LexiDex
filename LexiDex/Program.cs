using System.CommandLine;
using Spectre.Console;
using LexiDex;

namespace LexiDex;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var modelDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
        if (!File.Exists(Path.Combine(modelDir, "model.onnx")))
            modelDir = Path.Combine("Models");

        var rootCommand = new RootCommand("LexiDex — Local semantic search powered by BGE embeddings");

        // --- index command ---
        var indexDirArg = new Argument<string>("directory") { Description = "Directory to index" };
        var chunkSizeOpt = new Option<int>("--chunk-size") { Description = "Characters per chunk (default: 500)", DefaultValueFactory = _ => 500 };
        var overlapOpt = new Option<int>("--overlap") { Description = "Overlap characters between chunks (default: 50)", DefaultValueFactory = _ => 50 };
        var extensionsOpt = new Option<string>("--extensions") { Description = "Comma-separated file extensions (default: built-in list)", DefaultValueFactory = _ => string.Join(",", IndexOptions.DefaultExtensions) };
        var includeCommentsOpt = new Option<bool>("--include-comments") { Description = "Preserve comments in code files" };
        var forceOpt = new Option<bool>("--force") { Description = "Force full re-index" };

        var indexCommand = new Command("index", "Index files in a directory");
        indexCommand.Add(indexDirArg);
        indexCommand.Add(chunkSizeOpt);
        indexCommand.Add(overlapOpt);
        indexCommand.Add(extensionsOpt);
        indexCommand.Add(includeCommentsOpt);
        indexCommand.Add(forceOpt);

        indexCommand.SetAction(async (parseResult, ct) =>
        {
            var directory = parseResult.GetValue(indexDirArg);
            var options = new IndexOptions
            {
                Directory = Path.GetFullPath(directory!),
                ChunkSize = parseResult.GetValue(chunkSizeOpt),
                ChunkOverlap = parseResult.GetValue(overlapOpt),
                FileExtensions = parseResult.GetValue(extensionsOpt)!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                IncludeComments = parseResult.GetValue(includeCommentsOpt),
                Force = parseResult.GetValue(forceOpt)
            };

            if (!Directory.Exists(options.Directory))
            {
                AnsiConsole.MarkupLine("[red]Directory not found:[/] {0}", options.Directory);
                return 1;
            }

            AnsiConsole.Write(new FigletText("LexiDex").Color(Color.Blue));
            AnsiConsole.WriteLine();

            using var engine = new SearchEngine(modelDir);

            IndexingProgress? lastProgress = null;
            var progress = new Progress<IndexingProgress>(p => lastProgress = p);

            await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var indexingTask = ctx.AddTask("[cyan]Indexing files[/]");
                    var embeddingTask = ctx.AddTask("[green]Generating embeddings[/]");

                    var reporter = new Progress<IndexingProgress>(p =>
                    {
                        if (p.Total > 0 && p.Message.Contains("embeddings", StringComparison.OrdinalIgnoreCase))
                        {
                            embeddingTask.MaxValue = p.Total;
                            embeddingTask.Value = p.Current;
                        }
                        else if (p.Total > 0)
                        {
                            indexingTask.MaxValue = p.Total;
                            indexingTask.Value = Math.Max(indexingTask.Value, p.Current);
                            if (!string.IsNullOrEmpty(p.Message))
                                indexingTask.Description = $"[cyan]{p.Message}[/]";
                        }
                        else if (!string.IsNullOrEmpty(p.Message))
                        {
                            indexingTask.Description = $"[cyan]{p.Message}[/]";
                        }
                    });

                    await engine.IndexAsync(options.Directory, options, reporter);

                    indexingTask.StopTask();
                    embeddingTask.StopTask();
                });

            if (lastProgress != null)
                AnsiConsole.MarkupLine("[green]Done.[/] {0}", lastProgress.Message.EscapeMarkup());

            return 0;
        });

        rootCommand.Add(indexCommand);

        // --- search command ---
        var searchDirArg = new Argument<string>("directory") { Description = "Directory to search", DefaultValueFactory = _ => Directory.GetCurrentDirectory() };
        var queryArg = new Argument<string>("query") { Description = "Search query" };
        var topKOpt = new Option<int>("--top-k") { Description = "Number of results (default: 5)", DefaultValueFactory = _ => 5 };

        var searchCommand = new Command("search", "Search indexed files");
        searchCommand.Add(searchDirArg);
        searchCommand.Add(queryArg);
        searchCommand.Add(topKOpt);

        searchCommand.SetAction(async (parseResult, ct) =>
        {
            var dir = Path.GetFullPath(parseResult.GetValue(searchDirArg)!);
            var query = parseResult.GetValue(queryArg)!;
            var topK = parseResult.GetValue(topKOpt);

            AnsiConsole.Write(new FigletText("LexiDex").Color(Color.Blue));

            using var engine = new SearchEngine(modelDir);

            if (engine.IndexExists(dir))
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Loading index...", _ => engine.LoadExistingIndexAsync(dir));
            }
            else
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Building index...", _ => engine.IndexAsync(dir, new IndexOptions { Directory = dir }));
            }

            var results = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Searching for [yellow]{query.EscapeMarkup()}[/]...", _ => engine.SearchAsync(query, topK: topK));

            if (results.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No results found.[/]");
                return 0;
            }

            DisplayResults(results, query);
            await RunRepl(engine, results, topK);

            return 0;
        });

        rootCommand.Add(searchCommand);

        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }

    static void DisplayResults(List<SearchResult> results, string query)
    {
        AnsiConsole.WriteLine();
        var rule = new Rule($"[yellow]Results for: \"{query.EscapeMarkup()}\"[/]");
        AnsiConsole.Write(rule);

        var table = new Table();
        table.AddColumn("#");
        table.AddColumn("File");
        table.AddColumn("Score");
        table.AddColumn("Preview");

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var preview = r.Content.Length > 100 ? r.Content[..100] + "..." : r.Content;
            table.AddRow(
                $"[cyan]{i + 1}[/]",
                $"[yellow]{Path.GetFileName(r.SourceFile).EscapeMarkup()}[/]",
                $"[dim]{r.Score:F4}[/]",
                preview.EscapeMarkup()
            );
        }

        AnsiConsole.Write(table);
    }

    static async Task RunRepl(SearchEngine engine, List<SearchResult> results, int topK)
    {
        AnsiConsole.MarkupLine("[dim]Enter a number to see the full excerpt, a quoted string to search again, or anything else to quit.[/]");

        while (true)
        {
            var input = AnsiConsole.Prompt(new TextPrompt<string>("[blue]>[/]"));

            if (int.TryParse(input, out var selection))
            {
                var idx = selection - 1;
                if (idx >= 0 && idx < results.Count)
                {
                    var r = results[idx];
                    var panel = new Panel(r.Content.EscapeMarkup())
                        .Header($"[yellow]{Path.GetFileName(r.SourceFile)}[/] (chunk {r.ChunkIndex})")
                        .Border(BoxBorder.Rounded);
                    AnsiConsole.Write(panel);
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Invalid selection.[/] Choose 1-{0}.", results.Count.ToString());
                }
            }
            else if (input.StartsWith('"') && input.EndsWith('"') && input.Length >= 2)
            {
                var newQuery = input[1..^1];
                if (string.IsNullOrWhiteSpace(newQuery))
                    continue;

                results = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Searching for [yellow]{newQuery.EscapeMarkup()}[/]...", _ => engine.SearchAsync(newQuery, topK: topK));

                if (results.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No results found.[/]");
                    continue;
                }

                DisplayResults(results, newQuery);
            }
            else
            {
                break;
            }
        }
    }
}
