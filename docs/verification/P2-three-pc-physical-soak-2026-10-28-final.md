# Three-PC physical LAN soak: final field audit, 2026-10-28

The exact installed 1.0.43.0 run `f899ccb822fd4298ab08b68f6bf743c4`
executed from 09:00 to 12:00 UTC on first `R5-7600X`, second
`DESKTOP-BN535U0` and third `LARKBOX-X-1`. All three Windows scheduled tasks
ended `Ready` with exit code 0, and all services were Running at collection.
The first PC's launcher recorded six successful operation batches, the second
six and the third five; every active hour had at least one. The collected
operation journals contain respectively 30, 30 and 25 completed operations,
zero failed operations and zero stderr bytes. Each capture loop ended with
four of four requested snapshots and no issue.

| Capture UTC | Files on each PC | Directories | Native bytes on each PC | Common full-tree SHA-256 |
| --- | ---: | ---: | ---: | --- |
| 09:00 | 50,154 | 195 | 10,399,184,958 | `70F8577BF8775D14D240B1A6EEA4255D1869690216C9258106931BB667927FC3` |
| 10:00 | 50,167 | 223 | 10,399,485,686 | `81D9BD7B67C9205C515D7DF84A7345FC9ADF32035378A278E46CDE102D4ECD3A` |
| 11:00 | 50,174 | 223 | 10,399,781,933 | `17BE25CF72FEB66926E8059429E37B16E2A29C42C59DC789C757E838FFA29698` |
| 12:00 | 50,175 | 223 | 10,399,773,399 | `8214FB2643225B97989E3FAC947851B9E5C2C3385CB77209B0C3E77146E5BFA6` |

All four `compare-hour-*.json` reports have `match=true`, three machines and
zero issues. The final hour was collected early at 12:43 UTC because the user
set a 14:00 Warsaw start deadline for the next check. The snapshot files
themselves were captured at 12:00 UTC; early comparison did not shorten the
three-hour active window. `Collect-FreshSoakEvidence-09c.ps1` completed at
12:44 UTC and copied 30, 34 and 27 run files from the first, second and third
PC respectively, verifying every remote SHA-256 after copy. Its status is
`complete`, with three matching computer identities and no issues. The two
later duplicate collector tasks were disabled after this verified manual
collection. Evidence is retained under
`artifacts/field/physical-50k-20261027/soak-f899ccb822fd4298ab08b68f6bf743c4/`
and the sibling `soak-f899ccb822fd4298ab08b68f6bf743c4-final-collect-status.json`.

The second PC physically suspended at 09:32 UTC and resumed at 09:33:50.
Its service and Ethernet were Running/Up at the wake task; the first selected
authenticated direct LAN QUIC candidate appeared 16.278 seconds after wake.
At 10:32 UTC its Ethernet adapter was disabled while Wi-Fi stayed Up, and
Ethernet was restored at 10:34 UTC. The transition journal records PeerSync
Running throughout. The first PC used Tailscale to reach the second during
the Wi-Fi-only interval, so this run verifies data integrity through a Wi-Fi
switch but does not prove direct physical Wi-Fi transport or all-peer recovery
within 30 seconds. P2.5.5 remains open. The separate 9 GiB fresh-corpus
convergence-within-three-hours and installed-service 1 Gbit/s throughput
checks also remain open. Gate P2 as a whole is not claimed.
