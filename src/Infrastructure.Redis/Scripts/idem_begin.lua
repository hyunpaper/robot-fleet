-- 10 §4 TryBegin. KEYS[1] = idem:{batchId}   ARGV[1] = nowMs   ARGV[2] = ttlSec(600)   ARGV[3] = orphanMs(5000)
local ok = redis.call('SET', KEYS[1], 'pending:' .. ARGV[1], 'NX', 'EX', ARGV[2])
if ok then return {'BEGIN', ARGV[1]} end
local v = redis.call('GET', KEYS[1])
if not v then                                   -- 사이에 만료
  redis.call('SET', KEYS[1], 'pending:' .. ARGV[1], 'EX', ARGV[2])
  return {'BEGIN', ARGV[1]}
end
if v == 'done' then return {'DONE', '0'} end
local t = tonumber(string.sub(v, 9))
if tonumber(ARGV[1]) - t >= tonumber(ARGV[3]) then
  redis.call('SET', KEYS[1], 'pending:' .. ARGV[1], 'EX', ARGV[2])
  return {'TAKEOVER', ARGV[1]}
end
return {'BUSY', tostring(t)}
