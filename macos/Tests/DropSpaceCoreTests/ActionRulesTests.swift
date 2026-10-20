import Foundation
import Testing
import DropSpaceCore

@Test func imageSizingAndQRMatchFrozenBoundaries() throws {
    let half = try ImageRules.scaled(width:5,height:7,percent:50)
    #expect(half.width == 2 && half.height == 4)
    let fit = try ImageRules.fitted(sourceWidth:400,sourceHeight:200,width:100,height:100,keepAspectRatio:true)
    #expect(fit.width == 100 && fit.height == 50)
    #expect(throws:(any Error).self) { try ImageRules.validate(width:16385,height:1) }
    #expect(throws:(any Error).self) { try ImageRules.validate(width:8193,height:8192) }
    #expect(ImageRules.canEncodeQR(String(repeating:"a",count:1600)))
    #expect(!ImageRules.canEncodeQR(String(repeating:"a",count:1601)))
    #expect(!ImageRules.canEncodeQR(String(repeating:"中",count:534)))
    #expect(!ImageRules.canEncodeQR(" \n"))
}
@Test func quickActionProfilesPreserveCustomOrderAndEmptySlots() throws {
    let image = DropItem(source:.space,kind:.file,title:"photo.jpg",file:.init(path:"/photo.jpg"))
    let available = QuickActionRules.localCapabilities([image])
    let automatic = QuickActionRules.partition(available:available,items:[image],preferences:[:])
    #expect(automatic.primary == [.resizeImage,.convertImage,.stripMetadata])
    var preference = QuickActionPreference(); preference.automatic = false; preference.actions = [.generateQr,.compressZip,.hashSha256]
    let manual = QuickActionRules.partition(available:available,items:[image],preferences:["image":preference])
    #expect(manual.primary == [.compressZip,.hashSha256])
    preference.actions = []
    #expect(QuickActionRules.partition(available:available,items:[image],preferences:["image":preference]).primary.isEmpty)
    preference.actions = [nil, .hashSha256, nil]
    let restored = try JSONDecoder().decode(QuickActionPreference.self, from: JSONEncoder().encode(preference))
    #expect(restored.actions == [nil, .hashSha256, nil])
    #expect(QuickActionRules.partition(available:available,items:[image],preferences:["image":restored]).primary == [.hashSha256])
    var settings = AppSettings(); settings.quickActionPreferences["image"] = restored
    _ = try settings.validated()
    settings.quickActionPreferences["image"]?.actions = [.hashSha256, nil, .hashSha256]
    #expect(throws: (any Error).self) { try settings.validated() }
    let text = DropItem(source:.space,kind:.text,title:"text",text:"sample")
    #expect(QuickActionRules.localCapabilities([text]) == [.generateQr])
    #expect(QuickActionRules.profile([text,image]) == nil)
    #expect(QuickAction.allCases.count == 14)
}
