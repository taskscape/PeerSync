# Control/status PR merge acceptance

[PR #37](https://github.com/taskscape/PeerSync/pull/37) merged at
2026-10-19T20:00:19Z. Final submitted head
`f986fe9cb6c06a923f779d6082e16be6903d2a63` passed all five required jobs in
[run 39313959928](https://github.com/taskscape/PeerSync/actions/runs/39313959928):
x64 9m05s, ARM64 4m43s, Server 2022 10m38s, Server 2025 8m01s and fuzz 11m51s
(including its unchanged full 600-second fuzz execution).

The run includes the explicit CI partition documented in
P1-case-capacity-server-partition-2026-10-19.md: full Engine on Server 2022,
all but its single duplicate startup capacity method on Server 2025, and full
watcher in x64. No workflow test was removed; default local verification remains
complete. These results supersede earlier failed/cancelled heads, not their
historical evidence.

Review thread PRRT_kwDOVOejjM6uDWTi/comment 4317438500 identified shared log-run
retention. Its reproduced and tested fix is included in the final green head;
the thread was resolved only after all checks passed. No textual reviewer reply
was posted. Squash commit `6934850733ca74ba68c20351b1a5a0f088fb51cd` has exactly
the tested tree `8153aa3ac0645d2dcbe763630c7a1fbce4fc9b05`.

Main-push [run 39315657236](https://github.com/taskscape/PeerSync/actions/runs/39315657236)
passes all five required jobs on the exact squash above: x64 11m30s, ARM64
4m42s, Server 2022 8m06s, Server 2025 13m02s and fuzz 11m16s. The previous
c289313 main failure is superseded by this verified descendant, not relabelled
as a passing old run. Runtime host, rendered status/reconnect and shared
retention children meet their recorded acceptance. The installed-client child
still needs its missing/stopped/actual PeerSync integration evidence; the parent
also requires authenticated production transport. No phase gate is closed.
Installed production service supervision, authenticated production sync
transport, full app/installer and other P1 gate requirements remain open.

The directory branch already contained the final tested head. After verifying
both ancestry and exact head/main tree equality, it recorded the identical
squash ancestry without reapplying code; directory work remains unsubmitted.
