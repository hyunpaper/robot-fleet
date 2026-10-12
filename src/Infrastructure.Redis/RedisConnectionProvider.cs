using System.Reflection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace RobotFleet.Infrastructure.Redis;

/// 연결 실패를 던지지 않는다. 불통이면 null(호출자가 fail-open), 재연결은 클라이언트가 백그라운드에서 한다. 10 §4
public sealed class RedisConnectionProvider : IAsyncDisposable
{
    private readonly RedisOptions _options;
    private readonly ILogger<RedisConnectionProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionMultiplexer? _mux;

    public RedisConnectionProvider(RedisOptions options, ILogger<RedisConnectionProvider> logger)
    {
        _options = options;
        _logger = logger;
    }

    public RedisOptions Options => _options;

    public async Task<IDatabase?> TryGetDatabaseAsync()
    {
        if (_mux is null)
        {
            await _gate.WaitAsync();
            try
            {
                if (_mux is null)
                {
                    var config = ConfigurationOptions.Parse(_options.ConnectionString);
                    config.AbortOnConnectFail = false;   // 불통이어도 객체는 만들고 뒤에서 재연결
                    config.ConnectTimeout = 1000;
                    config.AsyncTimeout = (int)_options.CommandTimeout.TotalMilliseconds;
                    config.SyncTimeout = (int)_options.CommandTimeout.TotalMilliseconds;
                    config.ConnectRetry = 1;
                    _mux = await ConnectionMultiplexer.ConnectAsync(config);
                    _logger.LogInformation("Redis 연결 {Endpoints} (연결됨: {Connected})",
                        string.Join(',', config.EndPoints), _mux.IsConnected);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        return _mux.IsConnected ? _mux.GetDatabase() : null;
    }

    public string Key(string suffix) => _options.KeyPrefix + suffix;

    public static string Script(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"내장 스크립트 없음: {name}");
        return new StreamReader(stream).ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        if (_mux is not null) await _mux.DisposeAsync();
    }
}
