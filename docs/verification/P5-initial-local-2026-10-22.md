# P5 initial local verification — 2026-10-22

Scope: the P5 branch's Archive UI, device-approval production-host test,
seven-target fuzz harness, user/admin guides and rendezvous test-port
reliability fix. These checks ran from a modified worktree on the x64
development PC; they are local evidence, not a green hosted workflow or a
physical two-PC acceptance run.

## Results

| Check | Result |
| --- | --- |
| Locked Release solution build | Pass, zero warnings/errors |
| Boundary artifact verifier controls | Pass, 12 controls |
| Focused installed-host pending/approve/block test | Pass, 1/1 |
| App tests excluding the three known screenshot tests | Pass, 16/16 |
| Seven-target fuzz | Pass, 600.0018644 seconds, 218,082 seeds, zero crashes; `artifacts/fuzz/verification.json` |
| Canonical local verifier through Service | Core 148, Crypto 15, Protocol 77, Net 51, Engine 1,036, Storage 302, Control.Contracts 39, Service 39: all pass |
| App under canonical verifier | 16 pass, three WPF render assertions fail because each captured bitmap has zero opaque pixels; windows report visible/loaded and a presentation source. Captures: `artifacts/clock-ui/` |
| Rendezvous | 13/13 pass after test-only port allocation repair |
| Simulation | 17/17 pass; eight flat and four hierarchical property seeds; `artifacts/simulation/20261022T213231-860c237ff6254df1b4e065b175085fd4/` |
| Native/diagnostic/publish continuation | P0 probe script, QUIC diagnostic script and all six win-x64 spike publishes exit 0 |

The first new fuzz smoke failed on seed 0 because the harness's supposedly
valid Hello lacked required name/version fields. This was a test-fixture
error, corrected before the 600-second run. Its initial synthetic crash
artifact is retained under ignored `artifacts/fuzz/crashes/` and is not
evidence of a product parser crash.

The two initial Rendezvous failures occurred before Kestrel started: the
test helper selected an arbitrary TCP ephemeral port, but Windows excluded
the corresponding UDP port on all 32 attempts. The helper now probes both
protocols in the existing peer-test port range. The complete 13-test suite
then passed. This change is limited to the test helper.

The canonical `build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 30` stopped at
the App suite and therefore did not run its own Rendezvous/simulation/P0/
publish tail. Those exact downstream scripts were run separately as listed
above. The screenshot failures match the earlier detached-desktop condition;
the new Archive tab still needs rendered or installed interactive validation.

P5.1's 24-hour fuzz, P5.3's million-file/idle metrics, P5.4's service-account
USN probe, P5.7's signed auto-update, external security review and every
physical LAN/WAN/72-hour release gate remain open. No v1 or beta claim is
made from these local results.
