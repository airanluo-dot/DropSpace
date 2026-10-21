import CSpectrumBuffer
import Testing

@Test func spectrumBufferWrapsAndReportsDroppedAudioWithoutBlocking() throws {
    let ring = try #require(DSSpectrumBufferCreate())
    defer { DSSpectrumBufferDestroy(ring) }
    var output = [Float](repeating: 0, count: 20_000)
    for pass in 0..<4 {
        let input = (0..<10_000).map { Float(pass * 10_000 + $0) }
        input.withUnsafeBufferPointer { DSSpectrumBufferWrite(ring, $0.baseAddress, $0.count) }
        let count = output.withUnsafeMutableBufferPointer { DSSpectrumBufferRead(ring, $0.baseAddress, $0.count) }
        #expect(count == 10_000); #expect(Array(output.prefix(count)) == input)
    }
    let full = [Float](repeating: 0.25, count: 16_384)
    full.withUnsafeBufferPointer { DSSpectrumBufferWrite(ring, $0.baseAddress, $0.count) }
    [Float(0.75)].withUnsafeBufferPointer { DSSpectrumBufferWrite(ring, $0.baseAddress, $0.count) }
    #expect(DSSpectrumBufferDropped(ring) == 1)
    let count = output.withUnsafeMutableBufferPointer { DSSpectrumBufferRead(ring, $0.baseAddress, $0.count) }
    #expect(count == full.count); #expect(Array(output.prefix(count)) == full)
    #expect(DSSpectrumBufferLastPacketTime(ring) > 0)
    #expect(DSSpectrumMonotonicTime() >= DSSpectrumBufferLastPacketTime(ring))
    DSSpectrumBufferInvalidate(ring)
    #expect(DSSpectrumBufferIsValid(ring) == 0)
    full.withUnsafeBufferPointer { DSSpectrumBufferWrite(ring, $0.baseAddress, $0.count) }
    #expect(output.withUnsafeMutableBufferPointer { DSSpectrumBufferRead(ring, $0.baseAddress, $0.count) } == 0)
}
