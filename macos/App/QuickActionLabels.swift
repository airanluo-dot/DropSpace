import DropSpaceCore

extension QuickAction {
    var labelResource: String {
        switch self {
        case .hashSha256: "ActionHashSha256.Text"
        case .compressZip: "ActionCompressZip.Text"
        case .generateQr: "ActionGenerateQr.Text"
        case .resizeImage: "ActionResizeImageMenuItem.Text"
        case .convertImage: "ActionConvertImage.Text"
        case .stripMetadata: "ActionStripMetadata.Text"
        case .open: "OpenItemButton.Content"
        case .showInFolder: "ShowInFolderMenuItem.Text"
        case .copy: "CopyItemButton.Content"
        case .copyPath: "CopyPath"
        case .preview: "PreviewItemMenuItem.Text"
        case .sendToDevice: "SendToDeviceAction.Text"
        case .nearbyLink: "CreateNearbyLinkAction.Text"
        case .internetLink: "CreateSecureInternetLinkAction.Text"
        }
    }
}
