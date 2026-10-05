# Adapter code in the product repository

The canonical Worker SDK, runnable example, package template and conformance tools live in [MirrorPulse/adapter-template](https://github.com/MirrorPulse/adapter-template). The product references its fixed SDK package through `Directory.Packages.props`; SDK source is not copied here.

`official/` contains development integration projects for protocol providers. The installed product uses independently signed `.mpadapter` releases from the official Adapter repositories. These projects are compatibility implementations and do not establish that every official Worker supports v2 multi-root routing.

`samples/`, `template/` and `eng/` retain older development scaffolding. New providers should use the canonical repository, its language-neutral [v2 specification](https://github.com/MirrorPulse/adapter-template/blob/main/spec/worker-v2.md) and downloadable conformance runners.

See [SDK dependency verification](../docs/adapter-sdk.md) for the pinned package, local feed and update procedure.
