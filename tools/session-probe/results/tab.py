import json
ts = ("bare", "tmux", "zmx")
r = {t: json.load(open("run-%s.json" % t)) for t in ts}
print("EMIT")
for k in r["bare"]["emit"]:
    print("  %-10s | " % k + " | ".join("%-50s" % r[t]["emit"][k][:50] for t in ts))
print("QUERY (T/F = query bytes reached the outer terminal)")
for k in r["bare"]["query"]:
    cells = []
    for t in ts:
        q = r[t]["query"][k]
        cells.append("%s %-46s" % (str(q["reached_terminal"])[0], q["result"][:46]))
    print("  %-10s | " % k + " | ".join(cells))
for t in ts:
    print(t, "KEYS", json.dumps(r[t]["keys"]))
    print(t, "THRU", r[t]["throughput"], "attach_bytes", r[t]["attach_bytes"])
