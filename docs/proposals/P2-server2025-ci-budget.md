# Preserve the Server 2025 native scope with a measured job ceiling

Approved by the user on 2026-10-22 as a narrow exception to the Server 2025
job limit in the P1 native runner decision.

PR #44 run [39774850407](https://github.com/taskscape/PeerSync/actions/runs/39774850407)
passed the entire Windows Server 2022 native/Engine job in 619.506 seconds, and
Windows Server 2025 passed its 1 GiB native probes and virtual-account/pipe
checks. The Server 2025 Engine child reported 631 passing cases and no failing
assertion before the shared 840-second verifier budget terminated it. Its
focused build took 149.838 seconds; the Engine process was still active at the
deadline. The same scope passed in 760.850 seconds on the previous hosted run,
so the measured variance makes another unchanged 15-minute retry unreliable.

The earlier [P1 native runner decision](P1-native-server-critical-path.md)
explicitly preserves the 15-minute job limit. This proposal is a narrow
exception for the Server 2025 matrix entry only. Give that entry a 25-minute
Actions failure ceiling and a 24-minute script budget, retaining one minute
for upload and cleanup. Keep Server 2022 at 15/14 minutes. Keep the focused
build, all native and cross-account probes, every Server 2025 Engine case
except the already excluded duplicate 1,001-conflict capacity stress, the full
Server 2022 Engine scope, process ownership and failure evidence unchanged.
No extra job or test removal is proposed. Both jobs end when their work ends;
25 minutes is a ceiling, not a mandatory run duration. Record which budget
each job used in its parallel summary.

First validate a script-level positive/negative budget control and the exact
matrix values locally, then run one current-head hosted PR workflow. A timeout
or partial Engine log cannot count as a passing gate. This exception does not
affect the separate physical Gate P2 checks or the requirement for green
PR/main CI before merging and beginning P3.
