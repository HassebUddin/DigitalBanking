# Benchmark: transfer with partition drill on sindh

2026-10-23T21:39:50.402Z | 60s | 6 workers | synchronous commit | gateway http://localhost:5000

| Metric | Value |
|---|---|
| Requests | 210 (196 ok, 14 failed) |
| Throughput | 3.3 ok/s |
| Latency p50 / p95 / p99 / max | 804.7 / 3803.2 / 23130.9 / 23340.7 ms |
| Transfers accepted as pending (finished later by the saga) | 0 |
| Seconds with errors | 7 (t=24s..58s) |
| Ledger balanced after | true (unbalanced postings 0, mismatched accounts 0) |
| Money conserved (total deposits unchanged by transfers) | true |

| Operation | Requests | OK | p50 ms | p95 ms | p99 ms |
|---|---|---|---|---|---|
| transfer-cross | 178 | 165 | 842.2 | 3803.2 | 22920.9 |
| transfer-local | 32 | 31 | 779.2 | 1849.6 | 23340.7 |

| Error | Count |
|---|---|
| 503 service_unavailable: The change could not be confirmed on the standby yet (standby DigitalBanking_Sindh_B did not confirm within 3s). Please retry. | 4 |
| 503 service_unavailable: The change could not be confirmed on the standby yet (standby DigitalBanking_Sindh_A did not confirm within 3s). Please retry. | 2 |
| 503 service_unavailable: The change could not be confirmed on the standby yet (standby DigitalBanking_Punjab_A did not confirm within 3s). Please retry. | 4 |
| 503 service_unavailable: The change could not be confirmed on the standby yet (standby DigitalBanking_KP_B did not confirm within 3s). Please retry. | 3 |
| 503 service_unavailable: The change could not be confirmed on the standby yet (standby DigitalBanking_Balochistan_B did not confirm within 3s). Please retry. | 1 |

Drill events:
- t=20s: sindh-a PARTITIONED from coordinator
- t=29.5s: sindh-b promoted to primary
- t=40s: sindh-a partition healed

```
second  ok/failed
   0s    0/0
   1s  ######################  5/0
   2s  ##################  4/0
   3s  ########################################  9/0
   4s  ###############################  7/0
   5s  ###############################  7/0
   6s  ####################################  8/0
   7s  ###########################  6/0
   8s  ###############################  7/0
   9s  ####################################  8/0
  10s  ######################  5/0
  11s  ######################  5/0
  12s  ###########################  6/0
  13s  ###############################  7/0
  14s  ####################################  8/0
  15s  ###############################  7/0
  16s  ###########################  6/0
  17s  ####################################  8/0
  18s  ####################################  8/0
  19s  ###############################  7/0
  20s  ####################################  8/0
  21s  #############  3/0
  22s  ####  1/0
  23s    0/0
  24s  #### xxxxxxxxx  1/2
  25s    0/0
  26s    0/0
  27s   xxxx  0/1
  28s  ####  1/0
  29s    0/0
  30s    0/0
  31s   xxxx  0/1
  32s  ####  1/0
  33s    0/0
  34s    0/0
  35s    0/0
  36s    0/0
  37s    0/0
  38s    0/0
  39s    0/0
  40s    0/0
  41s    0/0
  42s  ######################  5/0
  43s  ###############################  7/0
  44s  ###########################  6/0
  45s  ########################################  9/0
  46s  ########################################  9/0
  47s  ######################  5/0
  48s  ###############################  7/0
  49s    0/0
  50s    0/0
  51s  ######### xxxx  2/1
  52s    0/0
  53s    0/0
  54s   xxxx  0/1
  55s   xxxxxxxxx  0/2
  56s    0/0
  57s    0/0
  58s   xxxxxxxxxxxxx  0/3
  59s    0/0
```