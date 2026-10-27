# Physical three-PC LAN soak, 2026-10-27

This is the setup record for Gate P2's three-hour physical recovery soak. It is **not a pass** until all four real hourly captures, the random-operation journals, actual sleep/resume and Wi-Fi switch, and three-machine comparisons are reviewed.

The fresh run ID is `cd253776057947ccb61f32923f68c9d1`; the first capture hour is `2026-10-27T15:00:00Z` and the active window ends at 18:00 UTC. Three one-time SYSTEM tasks were registered for 14:55 UTC and read back as Ready:

| PC | Label | Task |
| --- | --- | --- |
| R5-7600X | first | `PeerSync-PhysicalSoak-cd253776057947ccb61f32923f68c9d1-first` |
| DESKTOP-BN535U0 | second | `PeerSync-PhysicalSoak-cd253776057947ccb61f32923f68c9d1-second` |
| LARKBOX-X-1 | third | `PeerSync-PhysicalSoak-cd253776057947ccb61f32923f68c9d1-third` |

Before registration, all three installed 1.0.33.0 services were Running. The same preflight counted 10,396,520,008 content bytes on each synchronized root, below the 10 GiB cap. No prior physical-soak tasks or launcher journals were present. The earlier proposed 14:00 run `1f058b35090a426db3db0b3ec3b7aa2a` was retired without a coordinated launch and is not reused.

Each staged kit is outside its synchronized root at `C:\PeerSync-Gate-Evidence\20261027-soak-kit`. The corrected tree collector SHA-256 is `5C1E27BC7E9E75DA29D062C4D11346D9C294BB7963D9B30337609108D1BDED53`; the corrected content-budget walker SHA-256 is `713B9D5E5241E6A03AD0F2F87F2BCF0FC415CCCB88711AFEF7130591AC7A7578`. The launcher SHA-256 is `F3DEC779FAD31E1EAC94CABFD653E256CC0D1DC0A71E3BEABA68F5D955C6650F`. All three copies of each were verified before task registration. The task registration helper was corrected to use Windows' `-InputObject` parameter set without `-Description`; its final staged SHA-256 is `46A71E8364B828D18EFC96DDC3D2EB44974209E741D250033785FD3E51CE6220`.

The launcher schedules captures at hours 0, 1, 2 and 3, with bounded random-operation batches in minutes 20–45 of each active hour. Final read-only capture and comparison can finish after the active window per the [bounded requirement](../requirements/three-hour-ten-gib-test-budget.md). The physical network-change and resume actions, native service health, per-hour content budgets, and exact three-way hashes must still be recorded before the gate is checked. The setup receipts and preflight JSON are retained under ignored `artifacts/field/physical-50k-20261027/`; the original corpus's failed 13:00 capture remains separate [evidence](P2-three-pc-50k-retry-2026-10-27.md).
