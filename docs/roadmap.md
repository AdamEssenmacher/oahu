# Roadmap

Oahu is built in a planned order, so that each part lands on top of the parts it depends on. The [architecture](architecture.md) describes the design this order builds.

## How work lands

- Work lands in small pull requests. Each adds one discrete, named capability, with its tests and documentation, and `main` builds and passes its tests after every one.
- A milestone below is a grouping of related pull requests, not a pull request. Its pull requests are planned when the milestone starts, and the order within a milestone may shift.
- No package is published until the iOS release gate (milestone 13).

A checked box means the work is merged to `main`.

## Repository

- [x] Build skeleton: solution, central package management, warnings as errors, public API tracking, trim and AOT analyzers
- [x] Continuous integration: build and test on Linux, Windows and macOS; format check; pack
- [x] Contribution and security guidance, and issue and pull request templates
- [x] Dependabot updates for GitHub Actions and NuGet
- [ ] Architecture overview and roadmap

## The core

The core is the `AdamE.Oahu` package. Its milestones are the first seven [layers](architecture.md#layers), in the same order. None of them needs a device. The property-change adapter, also in `AdamE.Oahu`, comes after the iOS host (milestone 12).

- [ ] **1. Wire format.** The reader and writer for every value kind: booleans, `int`, `long`, `double`, strings, instants, durations, enums, records, unions, nullable values, lists and keyed-list batches. The format's test vectors, which every later implementation must pass. The first test project.
- [ ] **2. Contract vocabulary.** `[OahuContract]`; the four stream interfaces; keyed rows and list operations; structural equality for contract values.
- [ ] **3. Loop and sources.** `OahuLoop` and delivery turns; the state, editable, event and keyed-list sources; derived state; delivery order and what each stream kind delivers in a turn; the rule against changes off the UI thread; the test loop.
- [ ] **4. Exporter.** Subscription and replay; forwarding to a sink; member dispatch; the delivery turn before an outermost native call returns; edit settlement; method failures and faults; cancellation when a screen closes.
- [ ] **5. Contract model.** The model of a contract read from C# symbols; the canonical signature and the contract hash, with test vectors shared by everything that computes it.
- [ ] **6. Analyzer and C# generator.** Diagnostics for contracts that cannot cross the seam; the generated exporter and its registration; the analyzer and generator packed into `AdamE.Oahu`.
- [ ] **7. Hosting.** The public surface a host builds on, in the `AdamE.Oahu.Hosting` namespace: connecting a view model to a native screen, the contract check, containers and mounts, the teardown order, and reporting failures and faults.

## iOS

These milestones add the Swift side and the iOS host. The first preview packages ship at the release gate, for iOS 17 and later. They are planned in small pull requests the same way.

- [ ] **8. Swift runtime.** The Swift reader and writer, passing the format's test vectors; the fixed interface; the connection; events and keyed lists; the preview port; the SwiftUI container; the mount registry and mount view; text editing; the runtime built as an XCFramework for device and simulator. Adds Swift package tests to continuous integration, on the build machine first and on a simulator when the SwiftUI parts land.
- [ ] **9. Swift emitter.** The generated Swift for a contract: value types and their codecs, the model, and the screen provider. A conformance package that compiles generated Swift and passes vectors written by the C# side.
- [ ] **10. Build integration.** The native code generator, with one command line that writes its output or checks that it is current; the build targets that run it, build the app's Swift package into an XCFramework and package it with each build; the `AdamE.Oahu.Build` package. Tests for a cold build, an incremental build, a renamed contract that leaves no stale output, and a generation error that stops the native build.
- [ ] **11. iOS host.** The binding of the fixed interface; the port; screen lookup; showing a container or a mount in a .NET iOS app; the `AdamE.Oahu.iOS` package. A small sample app, built and run on a simulator in continuous integration.
- [ ] **12. Property-change adapter.** The public seam adapters build on; sources over an existing `INotifyPropertyChanged` view model, its commands and its collections.
- [ ] **13. iOS release gate.** Package inspection; a guide to setting up an iOS app by hand; a clean consumer app, built from the packed packages for device and simulator, with Native AOT, and run on a simulator; the release workflow; and the first preview packages of `AdamE.Oahu`, `AdamE.Oahu.Build` and `AdamE.Oahu.iOS`.
- [ ] **14. iOS 16.** Support for apps that opt in to iOS 16, in a later preview: a second form of the generated model, and the runtime's fallbacks for it.

## Android

Android follows iOS with the same three native layers. These milestones are less settled than those above and may change before they start.

- [ ] **15. Kotlin runtime.** The Kotlin reader and writer, passing the format's test vectors; the fixed interface over JNI; the connection; events and keyed lists; the preview port; the Compose container and mount. Adds Gradle builds to continuous integration.
- [ ] **16. Kotlin emitter.** The generated Kotlin for a contract, and its conformance suite.
- [ ] **17. Android host.** The binding, the port, screen lookup, and showing a container or a mount in a .NET Android app; the build targets for the app's Gradle module; the `AdamE.Oahu.Android` package.
- [ ] **18. Android release gate.** The checks of milestone 13 for Android, and the first preview package of `AdamE.Oahu.Android`.

## Not scheduled

These are expected to follow, with no commitment and no place in the order yet. Each needs a decision before it is scheduled.

- Adapters for Rx and DynamicData view models
- Services, by which C# calls native code that the app writes
- Navigation paths that keep a native stack of mounts in step with a view model
- Recording and playing back sessions
- Localization and culture
- Previews generated from C# fixtures
- Text fields inside keyed-list rows, and a text field that handles input methods
- An `oahu` command for setting up and diagnosing a project, and a project template
- Hosts for other platforms, including macOS
- Larger sample applications
