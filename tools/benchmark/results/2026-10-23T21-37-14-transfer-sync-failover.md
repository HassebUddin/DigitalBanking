# Benchmark: transfer with failover drill on punjab

2026-10-23T21:37:14.193Z | 60s | 6 workers | synchronous commit | gateway http://localhost:5000

| Metric | Value |
|---|---|
| Requests | 212 (171 ok, 41 failed) |
| Throughput | 2.9 ok/s |
| Latency p50 / p95 / p99 / max | 1330.3 / 4869.7 / 11035.1 / 11282.8 ms |
| Transfers accepted as pending (finished later by the saga) | 0 |
| Seconds with errors | 13 (t=3s..50s) |
| Ledger balanced after | true (unbalanced postings 0, mismatched accounts 0) |
| Money conserved (total deposits unchanged by transfers) | true |

| Operation | Requests | OK | p50 ms | p95 ms | p99 ms |
|---|---|---|---|---|---|
| transfer-cross | 186 | 149 | 1399.9 | 4931.1 | 11035.1 |
| transfer-local | 26 | 22 | 1245.8 | 4129.9 | 4764.7 |

| Error | Count |
|---|---|
| 503 service_unavailable: The change could not be confirmed on the standby yet (standby DigitalBanking_KP_B did not confirm within 3s). Please retry. | 6 |
| 503 service_unavailable: The change could not be confirmed on the standby yet (standby DigitalBanking_Punjab_B did not confirm within 3s). Please retry. | 3 |
| 503 service_unavailable: The change could not be confirmed on the standby yet (standby DigitalBanking_Sindh_B did not confirm within 3s). Please retry. | 5 |
| 503 service_unavailable: The change could not be confirmed on the standby yet (standby DigitalBanking_Balochistan_B did not confirm within 3s). Please retry. | 2 |
| 503 service_unavailable: This region is switching to its standby database. Please retry in a few seconds. | 25 |

Drill events:
- t=20s: punjab-a taken OFFLINE
- t=31.8s: punjab-b promoted to primary
- t=40s: punjab-a brought back ONLINE

```
second  ok/failed
   0s    0/0
   1s    0/0
   2s    0/0
   3s   xxxxxxxxxxxxxxxxx  0/6
   4s    0/0
   5s    0/0
   6s   xxxxxxxxxxxxxx  0/5
   7s    0/0
   8s    0/0
   9s  #########  3/0
  10s  #########  3/0
  11s  #########  3/0
  12s  #########  3/0
  13s  ######  2/0
  14s  ###########  4/0
  15s  #########  3/0
  16s  ######  2/0
  17s  ###########  4/0
  18s  ######  2/0
  19s  #########  3/0
  20s  ######### xxxxxxxxxxx  3/4
  21s  ###### xxxxxx  2/2
  22s  ###### xxx  2/1
  23s    0/0
  24s    0/0
  25s  ###### xxxxxxxxxxxxxx  2/5
  26s  ###### xxx  2/1
  27s  ################# xxxxxxxxxxxxxxxxxxxxxxx  6/8
  28s  ############## xxxxxxxxxxx  5/4
  29s    0/0
  30s    0/0
  31s   xxxxxx  0/2
  32s  #################  6/0
  33s  ###########  4/0
  34s  ####################  7/0
  35s  ###########  4/0
  36s  #################  6/0
  37s  #########  3/0
  38s  ##############  5/0
  39s  ###########  4/0
  40s  ####################  7/0
  41s  #########  3/0
  42s  #######################  8/0
  43s  #########  3/0
  44s  #########  3/0
  45s    0/0
  46s    0/0
  47s   xxx  0/1
  48s   xxx  0/1
  49s    0/0
  50s   xxx  0/1
  51s    0/0
  52s  ###  1/0
  53s  #########  3/0
  54s  ####################  7/0
  55s  ####################  7/0
  56s  #######################  8/0
  57s  #######################  8/0
  58s  #######################  8/0
  59s  #################  6/0
```