// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "DropSpaceMac",
    platforms: [.macOS("26.0")],
    products: [
        .library(name: "DropSpaceCore", targets: ["DropSpaceCore"]),
        .library(name: "DropSpaceInfrastructure", targets: ["DropSpaceInfrastructure"]),
        .library(name: "DropSpacePlatform", targets: ["DropSpacePlatform"]),
        .executable(name: "DropSpaceUpdateHelper", targets: ["DropSpaceUpdateHelper"]),
        .executable(name: "DropSpaceModuleSample", targets: ["DropSpaceModuleSample"]),
    ],
    targets: [
        .systemLibrary(name: "CSQLite"),
        .systemLibrary(name: "CZlib"),
        .target(name: "CSpectrumBuffer", linkerSettings: [.linkedFramework("CoreAudio")]),
        .target(name: "DropSpaceCore", resources: [.copy("Resources")]),
        .target(name: "DropSpaceInfrastructure", dependencies: ["DropSpaceCore", "CSQLite", "CZlib"]),
        .target(name: "DropSpacePlatform", dependencies: ["DropSpaceCore", "DropSpaceInfrastructure", "CSpectrumBuffer"]),
        .executableTarget(name: "DropSpaceUpdateHelper", dependencies: ["DropSpaceCore", "DropSpaceInfrastructure"]),
        .executableTarget(name: "DropSpaceModuleSample", dependencies: ["DropSpaceCore"], path: "Modules/Sample/Worker"),
        .testTarget(name: "DropSpaceCoreTests", dependencies: ["DropSpaceCore"], resources: [.copy("Fixtures")]),
        .testTarget(name: "DropSpaceInfrastructureTests", dependencies: ["DropSpaceInfrastructure"], resources: [.copy("Fixtures")]),
        .testTarget(name: "DropSpacePlatformTests", dependencies: ["DropSpacePlatform"]),
    ]
)
