using System.Security.Cryptography;

namespace RobotFleet.Shared.Messaging;

/// RFC 9562 UUIDv7. 같은 생성기에서 나온 값은 문자열 순서가 생성 순서와 같다(같은 ms 안에서도 증가). 03 batchId
public sealed class UuidV7(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private long _lastMs = -1;
    private ulong _counter;   // rand_b 62비트를 같은 ms 안의 단조 카운터로

    public string Next()
    {
        long ms;
        ulong counter;
        lock (_gate)
        {
            ms = Math.Max(_time.GetUtcNow().ToUnixTimeMilliseconds(), _lastMs);
            if (ms == _lastMs)
            {
                _counter++;
            }
            else
            {
                _lastMs = ms;
                Span<byte> seed = stackalloc byte[8];
                RandomNumberGenerator.Fill(seed);
                _counter = BitConverter.ToUInt64(seed) & 0x0FFF_FFFF_FFFF_FFFF;   // 넘침 여유를 남긴다
            }

            counter = _counter;
        }

        // 48비트 ms | 버전 7 | rand_a 12비트(0) | variant 10 | rand_b 62비트(= 단조 카운터)
        Span<byte> b = stackalloc byte[16];
        for (var i = 0; i < 6; i++)
            b[i] = (byte)(ms >> (40 - 8 * i));
        b[6] = 0x70;
        b[7] = 0x00;
        b[8] = (byte)(0x80 | (int)((counter >> 56) & 0x3F));
        for (var i = 0; i < 7; i++)
            b[9 + i] = (byte)(counter >> (48 - 8 * i));
        return Convert.ToHexString(b).ToLowerInvariant().Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-");
    }
}
