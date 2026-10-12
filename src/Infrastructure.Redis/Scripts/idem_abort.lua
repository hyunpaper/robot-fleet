-- 01 · 08 §9: 내가 쓴 pending일 때만 지운다. KEYS[1] = idem:{batchId}   ARGV[1] = "pending:{myMs}"
if redis.call('GET', KEYS[1]) == ARGV[1] then
  return redis.call('DEL', KEYS[1])
end
return 0
