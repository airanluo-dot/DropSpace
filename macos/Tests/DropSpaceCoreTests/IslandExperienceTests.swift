import Foundation
import Testing
import DropSpaceCore

@Test func islandAvailabilityPriorityAndManualPagesFollowFrozenCoordinator() {
    let now = Date(timeIntervalSince1970: 1000)
    var island = IslandExperience(), appearance = IslandAppearanceSettings()
    island.updateSettings(appearance, priority: "music", fullscreenAllows: true, now: now)
    island.updateFiles(2, now: now)
    #expect(island.current.state == .compact && island.current.page == .files && island.current.variant == .multipleFiles)
    island.updateMedia(available: true, playing: true, enabled: true, identity: "track", now: now)
    #expect(island.current.page == .music && island.current.content == .music)
    island.open(.files, now: now); island.updateMedia(available: true, playing: true, enabled: true, identity: "track-2", now: now)
    #expect(island.current.page == .files && island.current.state == .expanded && island.current.content == .files)
    island.select(.widgets, now: now); island.collapse(now: now)
    #expect(island.current.page == .widgets && island.current.content == .music)
    island.updateMedia(available: true, playing: false, enabled: true, identity: "track-2", now: now)
    #expect(island.current.page == .widgets && island.current.content == .files)
    island.updateSettings(appearance, priority: "space", fullscreenAllows: true, now: now)
    #expect(island.current.page == .files)
    island.updateFiles(0, now: now)
    #expect(island.current.page == .music)
    appearance.resident = true
    island.updateMedia(available: false, playing: false, enabled: true, identity: nil, now: now)
    island.updateSettings(appearance, priority: "space", fullscreenAllows: true, now: now)
    #expect(island.current.state == .compact && island.current.variant == .idle)
}

@Test func islandPauseDeadlineCannotBeExtendedByRefreshAndStaleDismissalCannotHideReopen() {
    let now = Date(timeIntervalSince1970: 1000)
    var island = IslandExperience()
    island.updateMedia(available: true, playing: true, enabled: true, identity: "track", now: now)
    island.updateMedia(available: true, playing: false, enabled: true, identity: "track", now: now)
    let deadline = island.current.nextDeadline, generation = island.hideGeneration
    #expect(deadline == now.addingTimeInterval(3))
    for tick in 1...29 { island.updateMedia(available: true, playing: false, enabled: true, identity: "track", now: now.addingTimeInterval(Double(tick) / 10)) }
    #expect(island.current.nextDeadline == deadline && island.hideGeneration == generation)
    island.deadline(generation: generation, expected: now.addingTimeInterval(3), now: now.addingTimeInterval(3))
    #expect(island.current.state == .dismissing)
    island.open(now: now.addingTimeInterval(3.1))
    island.completeDismissal(generation: generation, now: now.addingTimeInterval(3.2))
    #expect(island.current.state == .expanded)
    island.updateSettings(.init(), priority: "music", fullscreenAllows: false, now: now.addingTimeInterval(3.3))
    #expect(island.current.state == .expanded) // Explicit quick panel remains available.
}

@Test func islandDragAndActivityPreemptionRestoreChosenPageAndGeometry() {
    let now = Date(timeIntervalSince1970: 1000)
    var island = IslandExperience()
    island.open(.clipboard, now: now)
    island.beginDrag(visible: true, now: now)
    #expect(island.current.state == .expanded && island.expandedDropActive && island.current.page == .files)
    island.completeDrop(1, now: now)
    #expect(island.current.state == .expanded && island.current.page == .clipboard && !island.expandedDropActive)
    island.activities(notificationUntil: now.addingTimeInterval(5), volumeUntil: now.addingTimeInterval(2), now: now)
    #expect(island.current.state == .compact && island.current.content == .notification)
    island.beginDrag(visible: false, now: now)
    #expect(island.current.state == .dragApproaching && island.current.content == .files)
    island.dragReady(true, now: now); #expect(island.current.state == .dragReady)
    island.cancelDrag(now: now)
    #expect(island.current.content == .notification)
    island.activities(notificationUntil: nil, volumeUntil: nil, now: now.addingTimeInterval(5))
    #expect(island.current.state == .expanded && island.current.page == .clipboard)
    #expect(IslandPage.allCases == [.widgets, .music, .files, .clipboard])
    #expect(IslandGeometry.files(.expanded) == .init(width: 560, height: 340, radius: 28))
    #expect(IslandGeometry.files(.dragReady) == .init(width: 430, height: 92, radius: 30))
    #expect(IslandGeometry.compact(.multipleFiles).width == 200)
}
