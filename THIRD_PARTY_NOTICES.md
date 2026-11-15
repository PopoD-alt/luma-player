# Third-party components

Luma's own application code is proprietary under [LICENSE](LICENSE). Third-party
libraries retain their respective licenses and copyrights. Luma's license grants
the applicable LGPL permissions to modify the combined work for personal use,
replace or relink compatible libraries, and reverse engineer to debug those
modifications. Charging no money and publishing source for inspection do not
replace third-party license obligations.

## libVLC development runtimes

Luma has an optional dynamically loaded libVLC backend. The current package audit
is recorded in [ThirdParty/libvlc/packages.json](ThirdParty/libvlc/packages.json).
**A development candidate is not approved for redistribution.** The registry
blocks unreviewed stock candidates in public release artifacts. A separately
rebuilt, curated component must have its own exact approval record.
Separately installed VLC may be used for local development; its application,
plugins and dependencies are not thereby incorporated into Luma's license.

libVLC and VLCKit are VideoLAN projects. The library core is offered under
LGPL-2.1-or-later, while VLC's application and some modules/dependencies have
different terms. A stock VLC application bundle/ZIP must not be treated as an
LGPL-only dependency. The LGPL-labelled Windows NuGet package and the Android
Maven POM are useful provenance, but their labels alone do not complete an audit
of the delivered native files and their dependencies.

Candidate versions under inspection:

- macOS: official VLCKit 3.7.3 framework, reporting libVLC 3.0.23 and changeset
  `3.0.23-2-47-g4eb3701ba9`; upstream VLCKit snapshot `319ed2c0`, VLC base
  `79128878`, with 47 upstream patches. The framework is dynamically replaceable
  and contains native codec dependencies within that shared library. Inspection
  found GPL-2.0-or-later modules in the actual framework (including the dummy
  interface, mono mixer, rotate filter and syslog logger). Its LGPL-labelled
  COPYING file and GPL-disabled contrib build do not make the complete binary
  LGPL-only. This candidate is rejected for Luma's proprietary distribution;
  a rebuilt library excluding all GPL code is required for that route.
- Windows: `VideoLAN.LibVLC.Windows` 3.0.24, distinct from its `.GPL` variant;
  native libraries/plugins are separately loaded. Stock VLC 3.0.24 is allowed
  only as an external test fixture in this preparation workflow.
- Android: `org.videolan.android:libvlc-all:3.7.7`, a shared JNI/native library
  AAR. Its published Java sources do not contain the native VLC/contrib sources.
  The APK additionally needs a verified replacement/relinking route, dependency
  notices and corresponding native build materials before release approval.

## Curated macOS library

The original arm64 shared-library build uses VLC base `79128878` with the 47
VLCKit `319ed2c0` patches and targets macOS 14 or later. Contributed dependencies
are built with GPL, GNUv3 and nonfree profiles disabled. Each retained dynamic
plugin must have inspected LGPL2.1+ metadata and an expanded source/header
inventory; GPL-marked or uncertain modules are excluded conservatively. This is
a different binary from the stock framework above, with different coverage.
The exact module decisions, binary hashes and deployment targets are recorded
with that component's provenance. A successful playback test alone is not an
approval.

An approved curated runtime includes verbatim upstream/dependency notices and
`Sources/CorrespondingSource.tar`: the two pinned upstream source snapshots,
all 47 upstream patches, all 45 selected dependency source archives, exact
recipes and the original build/curation scripts. See the included
`audit/REBUILD.md` for reproduction. No codec implementation is claimed as Luma's
own. LGPL requirements and the applicable permissive notices remain in force;
the source aggregate can contain uncompiled GPL files with their own licenses.
Luma's original library preparation/build/audit scripts are separately permitted
under the included `LUMA-BUILD-SUPPORT-LICENSE.txt` (MIT); this exception does not
open-license the application itself.

Exact download URLs, SHA256 values, audit gaps and source archive completeness
are in the registry. Hashes identify the inspected bytes; they are not a legal
clearance or a substitute for a publisher signature. Native codec coverage,
patent rights, trademarks, region-specific requirements and media rights require
separate consideration. VideoLAN does not endorse Luma.

## Source and replacement information

Bundled playback components are based in part on the work of the FreeType Team.
FreeType is used under its FreeType License (FTL) option. Configurations that
include libjpeg are based in part on the work of the Independent JPEG Group.
The original license texts, copyright notices and source archives are retained
with the versioned component materials. These acknowledgements do not imply
endorsement of Luma by either project.

The original LGPL text is retained in [COPYING.LIB](ThirdParty/libvlc/COPYING.LIB),
and its referenced GPL text in [COPYING.GPL-2.0](ThirdParty/libvlc/COPYING.GPL-2.0).
For any approved bundled version, keep its complete corresponding source,
upstream patches, dependency sources, license notices, build scripts and version
metadata with the release source materials. A link to a moving upstream branch,
an incomplete Java sources JAR, or the Luma repository alone is insufficient.
Retain existing upstream notices verbatim. Do not remove GPL modules merely by
renaming files, and do not disable license-required user library replacement by
cryptographic allowlisting in the playback loader. The preparation gate verifies
our reviewed distribution inputs; it is not a restriction on a user's modified
compatible runtime.

The `Scripts/prepare-libvlc.py` command stages pinned candidates and records every
file. Its `verify --distribution` mode rejects incomplete audits and binds the
complete approved runtime inventory, including the corresponding-source bundle.
It does not automatically approve a package because it plays media, has a known
hash, carries an LGPL POM label, or appears in an upstream download directory.

## System components

macOS uses Apple system frameworks, including AVFoundation, AppKit, CoreMedia and
AudioToolbox, under their own terms. Windows uses Microsoft media facilities and
may distribute the .NET/WPF runtime in self-contained builds. The matching
[Windows runtime notices](Windows/THIRD_PARTY_NOTICES.md) and version-specific
Microsoft license files remain required. Android platform and AndroidX/Kotlin
components retain their own notices and terms, recorded by its build.

Primary references: [VideoLAN FAQ](https://www.videolan.org/support/faq.html),
[official libVLC packages](https://code.videolan.org/videolan/libvlc-nuget),
[VLCKit source](https://code.videolan.org/videolan/VLCKit),
[Android binding source](https://code.videolan.org/videolan/libvlcjni), and
[GNU LGPL 2.1](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html).
