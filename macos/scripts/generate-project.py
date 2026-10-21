#!/usr/bin/env python3
"""Deterministic Xcode project; native app imports the repository-local Swift package."""
import hashlib,pathlib,json
root=pathlib.Path(__file__).resolve().parents[1]
project=root/'DropSpace.xcodeproj'; project.mkdir(exist_ok=True)
def uid(s):return hashlib.sha256(s.encode()).hexdigest()[:24].upper()
objects={}
def add(k,t):objects[uid(k)]=t;return uid(k)
files=[]; builds=[]
for p in sorted(p for p in (root/'App').iterdir() if p.suffix in ('.swift', '.metal')):
    relative=p.relative_to(root).as_posix()
    kind = 'sourcecode.metal' if p.suffix == '.metal' else 'sourcecode.swift'
    f=add(relative,f'isa = PBXFileReference; lastKnownFileType = {kind}; path = "{relative}"; sourceTree = "<group>";')
    files.append(f);builds.append(add('build'+relative,f'isa = PBXBuildFile; fileRef = {f};'))
product=add('product','isa = PBXFileReference; explicitFileType = wrapper.application; path = DropSpace.app; sourceTree = BUILT_PRODUCTS_DIR;')
source=add('sources','isa = PBXSourcesBuildPhase; buildActionMask = 2147483647; files = ('+','.join(builds)+'); runOnlyForDeploymentPostprocessing = 0;')
package=add('package','isa = XCLocalSwiftPackageReference; relativePath = .;')
products=[];frameworks=[]
for name in ['DropSpaceCore','DropSpaceInfrastructure','DropSpacePlatform']:
    p=add(name,f'isa = XCSwiftPackageProductDependency; package = {package}; productName = {name};');products.append(p)
    frameworks.append(add('link'+name,f'isa = PBXBuildFile; productRef = {p};'))
framework=add('frameworks','isa = PBXFrameworksBuildPhase; buildActionMask = 2147483647; files = ('+','.join(frameworks)+'); runOnlyForDeploymentPostprocessing = 0;')
resources=add('resources','isa = PBXResourcesBuildPhase; buildActionMask = 2147483647; files = (); runOnlyForDeploymentPostprocessing = 0;')
embed_command='/usr/bin/python3 "$SRCROOT/scripts/embed-language-runtime.py" --app "$TARGET_BUILD_DIR/$WRAPPER_NAME"'
embed=add('embed-language', 'isa = PBXShellScriptBuildPhase; buildActionMask = 2147483647; files = (); inputPaths = (); outputPaths = (); alwaysOutOfDate = 1; name = "Embed verified language runtime"; runOnlyForDeploymentPostprocessing = 0; shellPath = /bin/sh; shellScript = '+json.dumps(embed_command)+';')
group=add('main','isa = PBXGroup; children = ('+','.join(files+[product])+'); sourceTree = "<group>";')
ui_files=[]; ui_builds=[]
for p in sorted((root/'UITests').glob('*.swift')):
    relative=p.relative_to(root).as_posix()
    f=add(relative,f'isa = PBXFileReference; lastKnownFileType = sourcecode.swift; path = "{relative}"; sourceTree = "<group>";')
    ui_files.append(f);ui_builds.append(add('build'+relative,f'isa = PBXBuildFile; fileRef = {f};'))
ui_product=add('ui-product','isa = PBXFileReference; explicitFileType = wrapper.cfbundle; path = DropSpaceUITests.xctest; sourceTree = BUILT_PRODUCTS_DIR;')
objects[group]='isa = PBXGroup; children = ('+','.join(files+ui_files+[product,ui_product])+'); sourceTree = "<group>";'
ui_source=add('ui-sources','isa = PBXSourcesBuildPhase; buildActionMask = 2147483647; files = ('+','.join(ui_builds)+'); runOnlyForDeploymentPostprocessing = 0;')
ui_link=add('ui-core-link',f'isa = PBXBuildFile; productRef = {products[0]};')
ui_framework=add('ui-frameworks',f'isa = PBXFrameworksBuildPhase; buildActionMask = 2147483647; files = ({ui_link}); runOnlyForDeploymentPostprocessing = 0;')
configs=[]
for config in ['Debug','Release']:
    settings='''ALWAYS_SEARCH_USER_PATHS = NO; ARCHS = arm64; SDKROOT = macosx; MACOSX_DEPLOYMENT_TARGET = 26.0; SWIFT_VERSION = 6.0;
        PRODUCT_BUNDLE_IDENTIFIER = "io.github.airanluo-dot.DropSpace.macOS"; PRODUCT_NAME = DropSpace;
        INFOPLIST_FILE = App/Info.plist; GENERATE_INFOPLIST_FILE = NO; CODE_SIGN_STYLE = Manual;
        CODE_SIGN_IDENTITY = "-"; ENABLE_HARDENED_RUNTIME = YES; ENABLE_APP_SANDBOX = NO;
        MARKETING_VERSION = 0.1.0; CURRENT_PROJECT_VERSION = 1; COMBINE_HIDPI_IMAGES = YES;
        LD_RUNPATH_SEARCH_PATHS = "$(inherited) @executable_path/../Frameworks";
        SWIFT_STRICT_CONCURRENCY = complete; SWIFT_ACTIVE_COMPILATION_CONDITIONS = "$(inherited) '''+('DEBUG' if config=='Debug' else '')+'";'
    if config=='Debug':settings+=' SWIFT_OPTIMIZATION_LEVEL = "-Onone"; DEBUG_INFORMATION_FORMAT = dwarf; ENABLE_TESTABILITY = YES;'
    else:settings+=' SWIFT_OPTIMIZATION_LEVEL = "-O"; DEBUG_INFORMATION_FORMAT = "dwarf-with-dsym";'
    configs.append(add(config,f'isa = XCBuildConfiguration; name = {config}; buildSettings = {{ {settings} }};'))
configuration=add('configurations','isa = XCConfigurationList; buildConfigurations = ('+','.join(configs)+'); defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;')
target=add('target',f'isa = PBXNativeTarget; name = DropSpace; productName = DropSpace; productType = "com.apple.product-type.application"; productReference = {product}; buildConfigurationList = {configuration}; buildPhases = ({source},{framework},{resources},{embed}); buildRules = (); dependencies = (); packageProductDependencies = ('+','.join(products)+');')
ui_configs=[]
for config in ['Debug','Release']:
    ui_configs.append(add('ui-'+config,f'''isa = XCBuildConfiguration; name = {config}; buildSettings = {{
        SDKROOT = macosx; ARCHS = arm64; MACOSX_DEPLOYMENT_TARGET = 26.0; SWIFT_VERSION = 6.0;
        PRODUCT_BUNDLE_IDENTIFIER = "io.github.airanluo-dot.DropSpace.macOS.UITests"; PRODUCT_NAME = DropSpaceUITests;
        GENERATE_INFOPLIST_FILE = YES; INFOPLIST_FILE = ""; CODE_SIGN_STYLE = Manual; CODE_SIGN_IDENTITY = "-"; ENABLE_APP_SANDBOX = NO;
        ENABLE_HARDENED_RUNTIME = NO;
        TEST_TARGET_NAME = DropSpace; SWIFT_OPTIMIZATION_LEVEL = "-Onone";
        LD_RUNPATH_SEARCH_PATHS = "$(inherited) @executable_path/../Frameworks @loader_path/../Frameworks";
    }};'''))
ui_configuration=add('ui-configurations','isa = XCConfigurationList; buildConfigurations = ('+','.join(ui_configs)+'); defaultConfigurationIsVisible = 0; defaultConfigurationName = Debug;')
dependency=add('ui-app-dependency',f'isa = PBXTargetDependency; target = {target};')
ui_target=add('ui-target',f'isa = PBXNativeTarget; name = DropSpaceUITests; productName = DropSpaceUITests; productType = "com.apple.product-type.bundle.ui-testing"; productReference = {ui_product}; buildConfigurationList = {ui_configuration}; buildPhases = ({ui_source},{ui_framework}); buildRules = (); dependencies = ({dependency}); packageProductDependencies = ({products[0]});')
proj=add('project',f'isa = PBXProject; attributes = {{ LastUpgradeCheck = 2700; TargetAttributes = {{ {ui_target} = {{ TestTargetID = {target}; }}; }}; }}; buildConfigurationList = {configuration}; compatibilityVersion = "Xcode 15.0"; developmentRegion = en; knownRegions = (en,Base); mainGroup = {group}; projectDirPath = ""; projectRoot = ""; targets = ({target},{ui_target}); packageReferences = ({package});')
(project/'project.pbxproj').write_text('// !$*UTF8*$!\n{ archiveVersion = 1; classes = {}; objectVersion = 60; objects = {\n'+''.join(f'{k} = {{ {v} }};\n' for k,v in sorted(objects.items()))+f'}}; rootObject = {proj}; }}\n')
schemes=project/'xcshareddata/xcschemes';schemes.mkdir(parents=True,exist_ok=True)
(schemes/'DropSpace.xcscheme').write_text(f'''<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion="2700" version="1.3"><BuildAction parallelizeBuildables="YES" buildImplicitDependencies="YES"><BuildActionEntries><BuildActionEntry buildForTesting="YES" buildForRunning="YES" buildForProfiling="YES" buildForArchiving="YES" buildForAnalyzing="YES"><BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{target}" BuildableName="DropSpace.app" BlueprintName="DropSpace" ReferencedContainer="container:DropSpace.xcodeproj"/></BuildActionEntry></BuildActionEntries></BuildAction><LaunchAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" launchStyle="0" useCustomWorkingDirectory="NO" ignoresPersistentStateOnLaunch="NO" debugDocumentVersioning="YES" debugServiceExtension="internal" allowLocationSimulation="YES"><BuildableProductRunnable runnableDebuggingMode="0"><BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{target}" BuildableName="DropSpace.app" BlueprintName="DropSpace" ReferencedContainer="container:DropSpace.xcodeproj"/></BuildableProductRunnable></LaunchAction><ProfileAction buildConfiguration="Release"/><AnalyzeAction buildConfiguration="Debug"/><ArchiveAction buildConfiguration="Release" revealArchiveInOrganizer="YES"/></Scheme>''')
print(project)
scheme=(schemes/'DropSpace.xcscheme').read_text()
test_action=f'''<TestAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" shouldUseLaunchSchemeArgsEnv="YES"><Testables><TestableReference skipped="NO"><BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{ui_target}" BuildableName="DropSpaceUITests.xctest" BlueprintName="DropSpaceUITests" ReferencedContainer="container:DropSpace.xcodeproj"/></TestableReference></Testables></TestAction>'''
(schemes/'DropSpace.xcscheme').write_text(scheme.replace('<LaunchAction',test_action+'<LaunchAction',1))
