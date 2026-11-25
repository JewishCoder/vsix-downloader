using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using VsixDownloader;

if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
{
    Console.Error.WriteLine("vsix-downloader нужно запустить в интерактивном терминале.");
    return 1;
}

using var stop = new CancellationTokenSource();
void OnCancel(object? sender, ConsoleCancelEventArgs e)
{
    e.Cancel = true;
    stop.Cancel();
}

Console.CancelKeyPress += OnCancel;
try
{
    return await RunAsync(stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    AnsiConsole.MarkupLine("[yellow]Отменено. Незавершённые загрузки сохранены для следующей попытки.[/]");
    return 130;
}
finally
{
    Console.CancelKeyPress -= OnCancel;
}

static async Task<int> RunAsync(CancellationToken cancellationToken)
{
    const string download = "Скачать расширение";
    const string configure = "Настройки";
    const string exit = "Выйти";

    AnsiConsole.Write(new Rule("[bold aqua]VSIX Downloader[/]").LeftJustified());
    Settings settings;
    try
    {
        settings = SettingsStore.Load();
    }
    catch (Exception ex) when (IsUserError(ex))
    {
        AnsiConsole.MarkupLine($"[yellow]Не удалось прочитать настройки. Используются значения по умолчанию.[/] {Ui(ex.Message)}");
        settings = new Settings();
    }

    while (true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var proxy = settings.GetProxy();
        AnsiConsole.MarkupLine($"[grey]Прокси[/] {Ui(proxy?.GetLeftPart(UriPartial.Authority) ?? "не используется")}");
        var action = await new SelectionPrompt<string>().Title("Выберите действие")
            .AddChoices(download, configure, exit).ShowAsync(AnsiConsole.Console, cancellationToken);
        try
        {
            switch (action)
            {
                case download:
                    await DownloadAsync(settings, cancellationToken);
                    break;
                case configure:
                    settings = await ConfigureAsync(settings, cancellationToken);
                    break;
                case exit:
                    return 0;
            }
        }
        catch (Exception ex) when (IsUserError(ex) && !cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine($"[red]{Ui(NetworkErrors.Describe(ex))}[/]");
        }
        AnsiConsole.WriteLine();
    }
}

static async Task DownloadAsync(Settings settings, CancellationToken cancellationToken)
{
    var id = (await new TextPrompt<string>("Extension ID [grey](publisher.extension, пусто для возврата)[/]")
        .AllowEmpty()
        .Validate(x => string.IsNullOrWhiteSpace(x) || MarketplaceClient.IsValidId(x.Trim())
            ? ValidationResult.Success()
            : ValidationResult.Error("Укажите ID в формате publisher.extension."))
        .ShowAsync(AnsiConsole.Console, cancellationToken)).Trim();
    if (id.Length == 0)
        return;

    using var http = settings.CreateClient();
    var gallery = new MarketplaceClient(http, settings);
    var catalog = await AnsiConsole.Status().StartAsync("Запрос к Microsoft Marketplace…", _ => gallery.FindAsync(id, cancellationToken));
    if (catalog is null)
    {
        AnsiConsole.MarkupLine($"[yellow]Расширение {Ui(id)} не найдено.[/]");
        return;
    }
    if (catalog.Builds.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]Marketplace не вернул доступных пакетов VSIX.[/]");
        return;
    }

    AnsiConsole.MarkupLine($"[bold]{Ui(catalog.DisplayName)}[/] [grey]({Ui(id)})[/]");
    var versions = catalog.Builds.GroupBy(x => (x.Version, x.IsPreRelease)).ToArray();
    var version = await new SelectionPrompt<IGrouping<(string Version, bool IsPreRelease), PackageBuild>>()
        .Title("Выберите версию")
        .PageSize(12)
        .EnableSearch()
        .SearchPlaceholderText("Введите номер версии")
        .AddChoices(versions)
        .UseConverter(v => $"{v.Key.Version}{(v.Key.IsPreRelease ? " (pre-release)" : "")} · сборок: {v.Count()}")
        .ShowAsync(AnsiConsole.Console, cancellationToken);

    var builds = version.ToArray();
    if (builds.Length > 1)
    {
        const string choose = "Выбрать платформы";
        const string all = "Скачать все сборки этой версии";
        var selection = await new SelectionPrompt<string>().Title("Какие сборки скачать?")
            .AddChoices(choose, all).ShowAsync(AnsiConsole.Console, cancellationToken);
        if (selection == choose)
            builds = (await new MultiSelectionPrompt<PackageBuild>()
                .Title("Выберите платформы")
                .InstructionsText("[grey]Пробел — отметить, Enter — подтвердить[/]")
                .Required()
                .UseConverter(x => x.PlatformLabel)
                .AddChoices(builds)
                .ShowAsync(AnsiConsole.Console, cancellationToken)).ToArray();
    }
    else
    {
        AnsiConsole.MarkupLine($"Сборка [aqua]{Ui(builds[0].PlatformLabel)}[/]");
    }

    var directory = await PromptDirectoryAsync(settings.OutputDirectory ?? Path.Combine(Environment.CurrentDirectory, "downloads"), cancellationToken);
    if (!await new ConfirmationPrompt($"Скачать файлов: {builds.Length}. Папка: {Ui(directory)}")
        .ShowAsync(AnsiConsole.Console, cancellationToken))
        return;

    var downloader = new DownloadService(http, settings);
    var results = new List<(string Platform, string Status, string Details)>();
    foreach (var build in builds)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .StartAsync(async context =>
                {
                    var task = context.AddTask(Ui(build.PlatformLabel), maxValue: 100);
                    var downloadResult = await downloader.DownloadAsync(build, directory, p =>
                    {
                        task.IsIndeterminate = p.TotalBytes is not > 0;
                        if (p.TotalBytes is > 0)
                            task.Value = Math.Clamp((double)p.Bytes / p.TotalBytes.Value * 100, 0, 99.9);
                        else
                            task.Value = 0;
                        task.Description = $"{Ui(build.PlatformLabel)} · {Ui(p.Status)} · {FormatBytes(p.Bytes)}" +
                            (p.TotalBytes is > 0 ? $" / {FormatBytes(p.TotalBytes.Value)}" : "");
                    }, cancellationToken);
                    task.Description = Ui(build.PlatformLabel);
                    task.IsIndeterminate = false;
                    task.Value = 100;
                    return downloadResult;
                });
            results.Add((build.PlatformLabel, result.AlreadyExists ? "Уже скачано" : "Скачано", result.Path));
        }
        catch (Exception ex) when (IsUserError(ex) && !cancellationToken.IsCancellationRequested)
        {
            results.Add((build.PlatformLabel, "Ошибка", NetworkErrors.Describe(ex)));
        }
    }

    var table = new Table().AddColumn("Платформа").AddColumn("Результат").AddColumn("Файл или причина");
    foreach (var row in results)
    {
        var color = row.Status == "Ошибка" ? "red" : "green";
        table.AddRow(Ui(row.Platform), $"[{color}]{Ui(row.Status)}[/]", Ui(row.Details));
    }
    AnsiConsole.Write(table);
}

static async Task<Settings> ConfigureAsync(Settings current, CancellationToken cancellationToken)
{
    const string proxySetting = "Прокси";
    const string timeouts = "Таймауты и попытки";
    const string directory = "Папка для загрузок";
    const string back = "Назад";

    var choice = await new SelectionPrompt<string>().Title("Настройки")
        .AddChoices(proxySetting, timeouts, directory, back).ShowAsync(AnsiConsole.Console, cancellationToken);
    Settings next;
    switch (choice)
    {
        case proxySetting:
            var proxy = await new TextPrompt<string>("URL прокси [grey](например, http://proxy:8080, пусто — отключить)[/]")
                .AllowEmpty()
                .Validate(x =>
                {
                    try
                    {
                        _ = (current with { ProxyUrl = x }).GetProxy();
                        return ValidationResult.Success();
                    }
                    catch (ArgumentException ex)
                    {
                        return ValidationResult.Error(ex.Message);
                    }
                }).ShowAsync(AnsiConsole.Console, cancellationToken);
            next = current with { ProxyUrl = string.IsNullOrWhiteSpace(proxy) ? null : proxy.Trim() };
            break;
        case timeouts:
            next = current with
            {
                ConnectTimeoutSeconds = await PromptIntAsync("Соединение, с", current.ConnectTimeoutSeconds, 3600, cancellationToken),
                ResponseTimeoutSeconds = await PromptIntAsync("Ожидание ответа, с", current.ResponseTimeoutSeconds, 3600, cancellationToken),
                IdleTimeoutSeconds = await PromptIntAsync("Допустимая пауза без данных, с", current.IdleTimeoutSeconds, 3600, cancellationToken),
                MaxAttempts = await PromptIntAsync("Число попыток", current.MaxAttempts, 10, cancellationToken)
            };
            break;
        case directory:
            next = current with
            {
                OutputDirectory = await PromptDirectoryAsync(current.OutputDirectory ?? Path.Combine(Environment.CurrentDirectory, "downloads"), cancellationToken)
            };
            break;
        default:
            return current;
    }

    SettingsStore.Save(next);
    AnsiConsole.MarkupLine($"[green]Настройки сохранены.[/] [grey]{Ui(SettingsStore.PathOnDisk)}[/]");
    return next;
}

static async Task<string> PromptDirectoryAsync(string defaultPath, CancellationToken cancellationToken)
{
    var path = await new TextPrompt<string>($"Папка для VSIX [grey](по умолчанию {Ui(defaultPath)})[/]")
        .DefaultValue(defaultPath)
        .ShowDefaultValue(false)
        .Validate(x =>
        {
            try
            {
                _ = Path.GetFullPath(x.Trim());
                return ValidationResult.Success();
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return ValidationResult.Error("Некорректный путь к папке.");
            }
        }).ShowAsync(AnsiConsole.Console, cancellationToken);
    return Path.GetFullPath(path.Trim());
}

static Task<int> PromptIntAsync(string label, int current, int limit, CancellationToken cancellationToken) =>
    new TextPrompt<int>(label).DefaultValue(current)
        .Validate(x => x >= 1 && x <= limit ? ValidationResult.Success() : ValidationResult.Error($"От 1 до {limit}."))
        .ShowAsync(AnsiConsole.Console, cancellationToken);

static string FormatBytes(long bytes) => (bytes / 1048576d).ToString("0.00", CultureInfo.InvariantCulture) + " MiB";

static string Ui(string value) => Markup.Escape(string.Concat(value.Where(c => !char.IsControl(c))));

static bool IsUserError(Exception ex) => ex is IOException or InvalidDataException or HttpRequestException or TimeoutException or
    UnauthorizedAccessException or ArgumentException or JsonException;
