# PeerSync physical field campaign closeout, 2026-10-28

The user required the final test to start by 14:00 and the current campaign
and handoff to end by 15:00 Europe/Warsaw on 2026-10-28. The final second-PC
mobile-hotspot observation began at about 13:58:35 Warsaw and ended at
14:03:35. Its temporary PeerSync-only Tailscale firewall rules and recovery
task were removed, Ethernet was restored, and the installed PeerSync service
was Running. No further physical test is scheduled for this campaign; the
hourly follow-up automation is paused.

| Check | Result at closeout | Evidence or limit |
| --- | --- | --- |
| Three-PC physical LAN soak | Passed | Installed 1.0.43.0 ran 09:00–12:00 UTC, with four matching full native trees, zero operation failures and complete copied evidence. See [final LAN audit](P2-three-pc-physical-soak-2026-10-28-final.md). |
| Samsung Galaxy hotspot, two-way relay sync | Passed as a five-minute observation | PeerSync on the second PC could not use Tailscale during the isolation window. Fresh files arrived in both directions with matching native SHA-256; the selected peer route was Relay. See [hotspot observation](P4-second-pc-hotspot-observation-2026-10-28.md). |
| 9 GiB mixed corpus on three physical PCs within three hours | Failed in the recorded bounded attempts | The earlier 100,000-file run missed the deadline with files absent on the third PC. The later 50,000-file run reached native receiver totals but missed the common timed three-way capture. The later LAN soak proves integrity of the settled tree, not the fresh-corpus deadline. See [corpus retry](P2-three-pc-50k-retry-2026-10-27.md). |
| Local current-source verification | Passed locally | The later Release verifier reported 1,839/1,839 product tests, 17/17 simulation methods and a 600-second seven-target fuzz run with zero crashes. See [local verifier](P5-current-head-local-2026-10-28.md). |
| Hosted current-head CI | Unverified | GitHub Actions denied the prior PR jobs before steps due to the account's Actions budget. A local pass is not a hosted pass; PR #49 remains draft and unmerged. |
| Remaining physical P2/P3/P4 checks | Open | Direct physical Wi-Fi all-peer recovery within 30 seconds, installed-service LAN throughput, unassisted app-only install/join, the full NAT matrix, two-home-network sync, deployed relay load/rate-cap and the three-hour WAN soak remain unverified. |
| Independent security review and v1.0 release | Open | No independent review, release-signed candidate, release-candidate LAN/WAN soaks or publication is claimed. |

The [formal closeout requirement](../requirements/field-campaign-closeout-2026-10-28.md)
ends this field campaign; it does not waive an open gate. The implementation
remains in draft PR #49. Any further physical run needs a separately
scheduled campaign or a new explicit requirement change.
