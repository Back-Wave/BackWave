# BackWave.Pro.Dashboard

The dashboard surface for [BackWave Pro](https://backwave.app). Installing this package adds a
**Workflows** tab and a live dependency-graph view to the BackWave dashboard. Nodes light up as jobs
run, succeed, fail, or cancel. The base dashboard alone shows no workflow surface; it appears because
this package is present.

> **Licensing.** Free to use for organizations under **$1M USD in annual revenue**; a license is required
> above that. Like the rest of Pro, it soft-fails, so an unlicensed host still renders the surface with a
> one-line notice. See the included EULA for terms.

```csharp
using BackWave.Pro;
using BackWave.Pro.Dashboard;

// Register after AddBackWavePro so the evaluated license is available to the dashboard surface.
builder.Services.AddBackWavePro(builder.Configuration["BackWave:ProLicense"]);
builder.Services.AddBackWaveProDashboard();

var app = builder.Build();
app.UseBackWaveDashboard("/backwave"); // the Workflows tab now appears here
```

Requires **BackWave.Dashboard** (the host dashboard) and **BackWave.Pro** (the Workflows feature). Full
documentation: https://backwave.app
