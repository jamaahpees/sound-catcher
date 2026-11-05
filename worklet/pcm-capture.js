class PcmCapture extends AudioWorkletProcessor {
  constructor() {
    super();
    this.blockSize = 4096;
    this.channels = 0;
    this.chunk = null;
    this.fill = 0;

    this.port.onmessage = (event) => {
      if (event.data === 'flush') {
        this.flush();
        this.port.postMessage({ type: 'flushed' });
      }
    };
  }

  flush() {
    if (!this.chunk || this.fill === 0) return;
    const data = this.chunk.map((channel) => channel.subarray(0, this.fill).slice());
    this.port.postMessage(
      { type: 'pcm', channels: this.channels, data },
      data.map((channel) => channel.buffer)
    );
    this.chunk = null;
    this.fill = 0;
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || input.length === 0 || !input[0] || input[0].length === 0) {
      return true;
    }

    const channelCount = input.length;
    if (!this.chunk || this.channels !== channelCount) {
      this.channels = channelCount;
      this.chunk = Array.from({ length: channelCount }, () => new Float32Array(this.blockSize));
      this.fill = 0;
    }

    const frames = input[0].length;
    for (let i = 0; i < frames; i += 1) {
      for (let c = 0; c < channelCount; c += 1) {
        this.chunk[c][this.fill] = input[c][i];
      }
      this.fill += 1;

      if (this.fill === this.blockSize) {
        const data = this.chunk;
        this.port.postMessage(
          { type: 'pcm', channels: channelCount, data },
          data.map((channel) => channel.buffer)
        );
        this.chunk = Array.from({ length: channelCount }, () => new Float32Array(this.blockSize));
        this.fill = 0;
      }
    }

    return true;
  }
}

registerProcessor('pcm-capture', PcmCapture);
