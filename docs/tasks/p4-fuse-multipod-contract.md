# p4-fuse-multipod-contract — cross-Pod RO FUSE fan-out contract (spec)

Task: `p4-fuse-multipod-contract` (P4 Stage B prep, spec/doc-only, `check=none`).

The full contract specification lives at
[`docs/plans/p4-fuse-multipod-contract.md`](../plans/p4-fuse-multipod-contract.md) (mirrors the
convention of `docs/plans/channel-architecture.md`). This file is the task-card pointer only.

Summary: specifies the cross-Pod RO FUSE fan-out contract that gostream's standalone-split and flux's
topology change must satisfy so `PhantomMaterialisingMediaSourceProvider` /
`GostreamPathResolver` (the plugin's existing stub-consumption code) keep working unmodified; flags the
narrow set of same-Pod-colocation assumptions (`GostreamBaseUrl`/`GostreamDiagnosticsBaseUrl`
`127.0.0.1` defaults) that DO need a follow-up config change once the split lands, and the one risk
(weaker cross-Pod attribute-cache/staleness guarantees than today's same-node `Bidirectional` hostPath
mount) to bound in the chosen mechanism.
