-- 10 §7. KEYS[1] = worker:members   ARGV[1] = workerId   ARGV[2] = nowMs   ARGV[3] = expireMs(30000)
redis.call('ZADD', KEYS[1], ARGV[2], ARGV[1])
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', tonumber(ARGV[2]) - tonumber(ARGV[3]))
return redis.call('ZRANGE', KEYS[1], 0, -1)
