# PhotoGrouper

A local desktop app that finds every photo of a person in your library.

Point it at a folder. It scans the files, detects the faces, works out which faces belong to the
same person, and lets you name each person once. From then on, "show me every photo of Alice" is a
question your photo library can actually answer.

Everything runs on your machine. No account, no upload, no cloud API. Your photographs are never
copied or moved by scanning — the app builds an index alongside them and leaves the originals
exactly where they are.

---

## Status

**Working today:** scan → detect faces → group into people → name them → find everyone.

| Milestone | State |
|---|---|
| M0 · Solution skeleton, photo index, library grid | Done |
| M1 · Vision layer: decoding, EXIF, detection, thumbnails | Done |
| M2 · ArcFace embeddings, clustering, People screen | Done |
| M3 · Second detector, switch machinery | Detector done; switch machinery not |
| M4 · Search, review queue, merge/split | Not started |
| M5 · Export: copy or move into per-person folders | Not started |
| M6 · Polish, resume hardening, settings | Partly |

Roughly 15,700 lines across 12 projects, with 246 tests.

---

## Platforms

Requires the **.NET 8 SDK**. The application code is platform-neutral; only the native imaging and
inference libraries differ, and the build selects the right ones for the machine it runs on.

| | Status | Inference |
|---|---|---|
| **Windows** x64 | Supported, developed and tested on | GPU via DirectML, falling back to CPU |
| **Linux** x64 | Configured, not yet run | CPU |
| **macOS** arm64 and x64 | Configured, not yet run | CPU |

Three packages vary by platform and are selected by MSBuild conditions: the OpenCV native runtime,
ImageMagick (referenced as the AnyCPU build, which carries all three platforms' libraries in one
package), and ONNX Runtime.

**GPU acceleration is Windows-only**, because DirectML is a Direct3D 12 API. Everywhere else
inference runs on the processor. That is slower — see the timings below — but it produces identical
vectors, so grouping quality is unaffected. On Linux with NVIDIA hardware, swapping the ONNX Runtime
reference for the CUDA package would restore it.

**Only the Windows build has actually been run.** The Linux and macOS references and platform guards
are in place, and the Windows build is unaffected by them, but nothing has been started on either.
Treat the first run there as something to verify rather than something known to work.

One decision a real port still has to make: **paths are compared case-insensitively**
(`COLLATE NOCASE` on `photos.path` and `scan_roots.path`). That is right on Windows and on a default
macOS volume, and wrong on Linux, where `Photo.jpg` and `photo.jpg` are two different files that
would be treated as one. Changing it is a schema migration, so it is worth settling before anything
depends on the current behaviour.

---

## Getting started

```
dotnet build
dotnet run --project src/PhotoGrouper.App
```

Then, in the app:

1. **Add folder** — choose a folder of photographs.
2. **Scan for photos** — indexes the files. Fast; it only reads names, sizes and timestamps.
3. **Find faces** — the first run downloads the detector and, on a GPU, compiles shaders. Expect a
   pause of roughly fifteen seconds before anything happens.
4. **People → Group faces into people** — downloads the recognition model (174 MB, once), then
   works out who is who.
5. Type a name on a group and press Enter.

Click any photo in the library to see the detected faces drawn over it, which is the quickest way
to confirm detection is behaving.

`dotnet test` runs the suite.

---

## How it works

```
folder ─▶ scan ─▶ decode ─▶ detect ─▶ align ─▶ embed ─▶ compare ─▶ group ─▶ name
         paths   pixels    boxes +   112×112   512-d    nearest    people   yours
                           landmarks  crop     vector   neighbours
```

Each stage writes its result to the database and advances the photo's state, so a run interrupted
at minute forty of forty-five resumes where it stopped rather than starting the library again.

**Detection** finds faces and five landmarks — the eyes, nose tip and mouth corners.

**Alignment** warps those five points onto a fixed template, so a face photographed at an angle
becomes comparable with the same face photographed straight on. Without it, an embedder reads two
pictures of one person, tilted differently, as two different people.

**Embedding** turns each aligned crop into 512 numbers. Two vectors are close when they are the
same person and far apart when they are not. On the reference photographs used during development,
the same person scored **0.62 – 0.81** and different people **−0.06 – 0.18**, so the grouping
threshold sits at **0.35**, in the middle of that gap rather than at either edge.

**Grouping** builds a graph of each face's nearest neighbours and lets labels propagate across it
(Chinese Whispers). This needs neither a number of people up front nor a density chosen in advance,
which matters because a photo library has an unknown number of people, some appearing four hundred
times and some twice.

---

## Models

Two files are downloaded on first use into the application data folder
(`%LOCALAPPDATA%\PhotoGrouper\models` on Windows, `~/.local/share/PhotoGrouper/models` on Linux),
each verified against a known SHA-256 before use.

| Purpose | Model | Size | Licence |
|---|---|---|---|
| Detection (default) | YuNet | 0.2 MB | Apache-2.0 |
| Detection (optional) | SCRFD-10GF | 17 MB | **Non-commercial research only** |
| Recognition | ArcFace R50 (WebFace600K) | 174 MB | **Non-commercial research only** |

> **Licensing.** InsightFace's pretrained weights — ArcFace and SCRFD — are licensed for
> non-commercial research use only. Since ArcFace is required for grouping, **this application as
> built cannot be distributed commercially.** Doing so would mean replacing the recognition model
> with a permissively licensed one, which the provider abstraction is designed to allow.

### Measured performance

Measured on Windows with an NVIDIA T550 laptop GPU (4 GB) and a 16-core CPU, per image or per
face. Only the CPU column applies on other platforms.

| Stage | CPU | DirectML |
|---|---|---|
| JPEG decode + EXIF | 30–60 ms | — |
| YuNet detection | 18.7 ms | **8.3 ms** |
| SCRFD-10GF detection | 59.2 ms | **35.4 ms** |
| ArcFace embedding, per face | 68 ms (batch 8) | **38 ms (batch 1)** |

Two findings worth knowing before planning a large run:

- **ArcFace cannot be batched on DirectML.** Any batch above one fails inside a batch-normalisation
  node on this hardware, and was unreliable before failing outright. Batch size is fixed at one on
  the GPU path and eight on the CPU path.
- **Embedding dominates.** A 50,000-photo library is on the order of an hour, most of it here.
  Detector choice is worth minutes; the embedder is worth the rest.

---

## Architecture

Clean Architecture, four layers, dependencies pointing inward only.

```
src/
  PhotoGrouper.Domain/                    entities, value objects, UUIDv7 identity
                                          — references no NuGet package at all
  PhotoGrouper.Application/               use cases and ports (interfaces)
                                          — references Domain and nothing else
  PhotoGrouper.Infrastructure.Storage.Sqlite/   ┐
  PhotoGrouper.Infrastructure.Vision/           │ adapters implementing the ports
  PhotoGrouper.Infrastructure.Imaging/          │
  PhotoGrouper.Infrastructure.FileSystem/       ┘
  PhotoGrouper.App/                       Avalonia UI and the single composition root
```

The boundaries are **enforced by tests**, not by convention. `PhotoGrouper.Architecture.Tests` uses
NetArchTest to assert that Domain references nothing outside the base class library, that
Application references only Domain, that neither mentions Avalonia, OpenCV, ONNX Runtime or SQLite,
and that no view model reaches a repository. A stray `using` fails the build rather than quietly
eroding the design.

Two consequences of that worth naming, because they look like over-engineering until you know why:

- **Pixels cross layer boundaries as `ImageBuffer`**, a plain byte buffer, not as OpenCV's `Mat`.
  A `Mat`-typed port would drag a native imaging library into the application layer.
- **Storage is reached only through ports** with intent-revealing methods — no `IQueryable`, no
  exposed connection. That is what keeps the backend replaceable, and `PhotoGrouper.Contracts.Tests`
  holds abstract suites any storage adapter must pass unchanged.

### Testing

| Project | Covers |
|---|---|
| `Domain.Tests` | Value objects, identity, geometry |
| `Application.Tests` | Use cases against fake ports — no disk, no models, no database |
| `Contracts.Tests` | Abstract suites every storage adapter must pass |
| `Infrastructure.Tests` | The SQLite, imaging and vision adapters |
| `Architecture.Tests` | The layer dependency rule |

The tests worth understanding are the ones guarding **silent** failures. Wrong channel order, a
mirrored alignment, a forgotten unit-normalisation, or a permuted landmark order all produce a
perfectly well-formed 512-float vector that simply does not describe the face. Nothing throws;
grouping just quietly stops working. So landmark ordering, the alignment transform and the
preprocessing constants are pinned by exact-value tests rather than by behavioural ones.

---

## What is stored, and why

The database lives in the application data folder — `%LOCALAPPDATA%\PhotoGrouper\library.db` on
Windows, `~/.local/share/PhotoGrouper/library.db` on Linux — with thumbnails beside it.

One rule decides what earns a table: **store only what is expensive to recompute, or impossible to
recompute.**

- *Expensive* — decoding, detection and embedding cost tens of minutes for a large library.
- *Impossible* — a person's name, a confirmed match, a rejection. No algorithm regenerates these.
- *Neither* — the neighbour graph and decoded pixels are never stored.

| Table | Holds | Why |
|---|---|---|
| `photos` | Path, size, modified time, dimensions, orientation, EXIF, state | Path + size + time is the incremental-scan key; `state` is what makes scanning resumable |
| `photo_detections` | Which detector examined which photo, and how many faces it found | Detection is per-detector; a face **count** distinguishes "examined, found nobody" from "not yet examined" |
| `faces` | Box, landmarks, quality, detector, person | A photo has 0..N faces; this is what makes "who is in this photo" expressible |
| `face_embeddings` | The 512-float vector per (face, embedder) | Separate from `faces` because vectors from different models are not comparable, dimensionality varies, and most queries never read one |
| `clusters` | Algorithmic groups, before naming | Derived, but re-deriving costs minutes |
| `persons` | Your names, plus a cached centroid | The only irreplaceable data in the file |
| `face_links` | Must-link / cannot-link decisions from review | Pure user judgement; must survive re-grouping |
| `export_runs`, `export_ops` | The undo journal for copy and move | Unused until M5 |

Embeddings dominate the size: 2 KB per face, so roughly 150 MB of vectors for a 50,000-photo
library against about 25 MB for everything else.

Thumbnails are JPEGs on disk rather than blobs in the database, because image data would bloat the
file and slow every vacuum and backup. They are disposable and rebuild on demand.

**Settings → Clear library** empties everything and reclaims the space. It deletes rows rather than
the file, because the database is open while the app runs, then checkpoints the write-ahead log and
vacuums — all three steps, or the file ends up larger after clearing than before.

---

## Known gaps

Honest list of what does not work yet.

- **Deleted photos are never noticed.** The scanner only adds and updates. A file deleted from disk
  keeps its row, keeps its cached thumbnail, and still counts toward a person — so the library
  reports photos that no longer exist. A missing *folder* is deliberately ignored, since an
  unplugged drive must not destroy naming work.
- **Moved or renamed files become duplicates**, rather than being re-linked by content hash.
- **No review queue.** Borderline matches are assigned automatically with no way to confirm them.
  `face_links` is wired through clustering and tested, but nothing writes to it yet.
- **No search screen and no export.** M4 and M5.
- **The library grid loads every photo into memory.** Fine at current scale; needs paging before
  50,000 is realistic.
- **RAW and video are ignored.** The decoder abstraction leaves room for RAW; video is out of scope.
- **Paths are compared case-insensitively.** Correct on Windows and on a default macOS volume,
  wrong on Linux, where `Photo.jpg` and `photo.jpg` are two different files and would be treated as
  one. Changing it means a schema migration, so it is worth deciding before anyone relies on it.
- **Only the Windows build has actually been run.** Linux and macOS are configured but unverified.

---

## Licence

The code is yours to license. The **models are not** — see the licensing note above. Any
distribution decision has to account for the InsightFace weights being non-commercial.
