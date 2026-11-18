# PR #124 review

Review authorized by the user's explicit request (lessons L-033 and L-047). No implementation changes.

- [x] Identify newest open PR and pin head/base: fae64a8911e184b66595ab06223d6ff549f3de66 / 7985d2d566649e471e0d1dc070addfa3fba35ef2.
- [x] Read title, description, full diff, commit history, requirements, existing discussion, and CI status.
- [x] Architect review of HTTP/shared proxy routing; independent WebSocket review; QA and adversarial review of tests and security boundaries.
- [x] Run proportionate checks and reproduce material concerns; separate regressions from documented limitations and existing bugs.
- [x] Recheck current PR head, post a precise review comment with evidence and requested changes, or a brief approving comment.
- [x] Record results and report the posted review link.

## Review results

CI run 44232307259 succeeded at the reviewed head. No prior review comments. Fourteen commits have descriptive subjects; the 37-file change is cohesive, predominantly tests and documentation.

Confirmed P2: the HTTP/WebSocket connect callbacks treat host/port equality with the trusted proxy as sufficient to skip private-network filtering, including when WebProxy bypasses the destination and sends directly. With a localhost-named proxy, a matching bypass, ResolveDnsNames=false, and no AllowedHosts, the HTTP probe sends an origin-form request to the proxy itself. The shipped ProxiedLiteralProbes.Pinned_a_bypassed_destination_naming_the_proxy_is_dialled_unfiltered test explicitly expects this unsafe behavior and passed during review. The plan's ADV-P7 acceptance does not resolve the contradiction with the public promise of a full connect-time guard for bypassed destinations. Requested correction: distinguish actual proxy routing from direct routing and test refusal on both transports.

Fresh Release builds of InteropTests and Core.Tests succeeded with /warnaserror, --no-restore, --disable-build-servers, -m:1 and -p:UseSharedCompilation=false: zero warnings/errors. Broad Interop transport tests: 477 passed, 2 skipped, zero failures. Core outbound guard tests: 100 passed, zero skipped/failures. A standalone WebSocket probe at /private/tmp/pr124-ws-review also demonstrated the direct GET to /internal-admin with the collision configuration; the no-proxy control made no TCP connection. The original stalled build process was terminated without changing source.

Publication: exact head rechecked. GitHub integration returned 403 Resource not accessible by integration for both review creation and conversation comment creation. Authenticated GitHub CLI successfully posted the requested review as a PR conversation comment: https://github.com/moisesja/didcomm-dotnet/pull/124#issuecomment-6983407685. Disposition: changes requested, one P2 finding; no source/test/product-doc/CHANGELOG changes made for this review-only task.
