using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using OmronUpsd;

switch (args.FirstOrDefault()?.ToLowerInvariant())
{
    case "install":
        return ServiceInstaller.Install();
    case "uninstall":
        return ServiceInstaller.Uninstall();
    case "status":
        return await PrintStatusAsync();
    case null or "run":
        break;
    default:
        Console.Error.WriteLine("usage: omronupsd.exe [run | install | uninstall | status]");
        return 2;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args.Skip(1).ToArray(),
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(o => o.ServiceName = AppInfo.ServiceName);
builder.Logging.AddProvider(new FileLoggerProvider());
builder.Services.Configure<EventLogSettings>(s => s.SourceName = AppInfo.ServiceName);

builder.Services.AddOptions<ServiceOptions>().BindConfiguration(ServiceOptions.Section).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ServiceOptions>, ServiceOptionsValidator>();
builder.Services.AddSingleton<ISystemShutdown, SystemShutdown.Windows>();

var role = builder.Configuration.GetSection(ServiceOptions.Section).Get<ServiceOptions>()?.Role ?? Role.Master;
if (role == Role.Master)
{
    builder.Services.AddSingleton<IUpsDevice, UpsDevice>();
    builder.Services.AddSingleton<MasterStatus>();
    builder.Services.AddSingleton<ClientHub>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ClientHub>());
    builder.Services.AddHostedService<MasterWorker>();
    builder.Services.AddHostedService<StatusServer>();
}
else
{
    builder.Services.AddHostedService<ClientWorker>();
}

try
{
    await builder.Build().RunAsync();
    return 0;
}
catch (OptionsValidationException ex)
{
    Console.Error.WriteLine("設定エラー: " + string.Join(" / ", ex.Failures));
    return 1;
}

static async Task<int> PrintStatusAsync()
{
    var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).Build();
    var port = config.GetSection(ServiceOptions.Section).Get<ServiceOptions>()?.Master.StatusPort ?? new MasterOptions().StatusPort;
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        var json = await http.GetStringAsync($"http://127.0.0.1:{port}/status");
        Console.WriteLine(JsonSerializer.Serialize(JsonDocument.Parse(json), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine($"ステータス取得失敗: {ex.Message}");
        return 1;
    }
}
