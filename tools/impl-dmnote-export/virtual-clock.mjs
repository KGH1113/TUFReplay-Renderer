/** A monotonic clock whose rAF cadence is the requested output frame cadence. */
export class VirtualClock {
  nowMs = 0;
  nextId = 1;
  timers = new Map();
  frames = new Map();
  afterTask = () => {};

  schedule(callback, delay = 0, repeat = false, args = []) {
    if (typeof callback !== 'function') throw new Error('String timers are unsupported during export.');
    const interval = Math.max(repeat ? 0.001 : 0, Number(delay) || 0);
    const id = this.nextId++;
    this.timers.set(id, { callback, due: this.nowMs + interval, interval: repeat ? interval : null, args });
    return id;
  }

  advanceTo(targetMs) {
    if (!Number.isFinite(targetMs) || targetMs < this.nowMs) throw new Error('Export time must be monotonic.');
    let steps = 0;
    for (;;) {
      let selected = null;
      for (const [id, timer] of this.timers) {
        if (timer.due <= targetMs && (!selected || timer.due < selected.timer.due || (timer.due === selected.timer.due && id < selected.id))) selected = { id, timer };
      }
      if (!selected) break;
      if (++steps > 100000) throw new Error('Export timer queue did not settle.');
      const { id, timer } = selected;
      this.nowMs = timer.due;
      if (timer.interval === null) this.timers.delete(id);
      else timer.due += timer.interval;
      timer.callback(...timer.args);
      this.afterTask();
    }
    this.nowMs = targetMs;
  }

  sampleFrame() {
    const frames = [...this.frames];
    this.frames.clear();
    for (const [, callback] of frames) {
      callback(this.nowMs);
      this.afterTask();
    }
  }

  install(target = globalThis) {
    const nativeDate = target.Date;
    const nativeTimers = Object.fromEntries(['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'requestAnimationFrame', 'cancelAnimationFrame'].map(key => [key, target[key]]));
    const oldNow = Object.getOwnPropertyDescriptor(target.performance, 'now');
    const clock = this;
    const epoch = 946684800000;
    target.Date = class extends nativeDate {
      constructor(...args) { super(...(args.length ? args : [epoch + clock.nowMs])); }
      static now() { return epoch + clock.nowMs; }
    };
    Object.defineProperty(target.performance, 'now', { configurable: true, value: () => clock.nowMs });
    target.setTimeout = (callback, delay, ...args) => clock.schedule(callback, delay, false, args);
    target.setInterval = (callback, delay, ...args) => clock.schedule(callback, delay, true, args);
    target.clearTimeout = target.clearInterval = id => clock.timers.delete(id);
    target.requestAnimationFrame = callback => { const id = clock.nextId++; clock.frames.set(id, callback); return id; };
    target.cancelAnimationFrame = id => clock.frames.delete(id);
    return () => {
      this.timers.clear(); this.frames.clear();
      target.Date = nativeDate;
      Object.assign(target, nativeTimers);
      if (oldNow) Object.defineProperty(target.performance, 'now', oldNow);
      else delete target.performance.now;
    };
  }
}
