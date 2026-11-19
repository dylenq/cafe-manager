(function () {
    'use strict';

    var STORAGE_KEY = 'cafe.alerts.muted';
    var ctx = null;
    var memoryMuted = false;

    function getContext() {
        try {
            if (!ctx) {
                var Ctor = window.AudioContext || window.webkitAudioContext;
                if (!Ctor) {
                    return null;
                }
                ctx = new Ctor();
            }
            return ctx;
        } catch (e) {
            return null;
        }
    }

    function isMuted() {
        try {
            var value = window.localStorage.getItem(STORAGE_KEY);
            if (value !== null) {
                return value === '1';
            }
        } catch (e) {
            // storage blocked: fall back to memory
        }
        return memoryMuted;
    }

    function setMuted(muted) {
        memoryMuted = !!muted;
        try {
            window.localStorage.setItem(STORAGE_KEY, muted ? '1' : '0');
        } catch (e) {
            // storage blocked: the in-memory value still applies for this page
        }
    }

    function isUnlocked() {
        return !!ctx && ctx.state === 'running';
    }

    // Must be called from a user gesture (click) so the browser lets audio start. Returns a promise that
    // resolves to whether the context is running once resume() has settled.
    function unlock() {
        try {
            var c = getContext();
            if (!c) {
                return Promise.resolve(false);
            }
            if (c.state === 'running') {
                return Promise.resolve(true);
            }
            return Promise.resolve(c.resume())
                .then(function () { return c.state === 'running'; })
                .catch(function () { return false; });
        } catch (e) {
            return Promise.resolve(false);
        }
    }

    function tone(c, freq, startAt, duration) {
        var osc = c.createOscillator();
        var gain = c.createGain();
        osc.type = 'sine';
        osc.frequency.value = freq;
        gain.gain.setValueAtTime(0.0001, startAt);
        gain.gain.exponentialRampToValueAtTime(0.3, startAt + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, startAt + duration);
        osc.connect(gain);
        gain.connect(c.destination);
        osc.start(startAt);
        osc.stop(startAt + duration + 0.05);
    }

    // kind: 'warning' = one 880 Hz beep, 'timeup' = three 1200 Hz beeps. Returns whether sound was played.
    function beep(kind) {
        try {
            if (isMuted() || !isUnlocked()) {
                return false;
            }
            var t = ctx.currentTime;
            if (kind === 'timeup') {
                tone(ctx, 1200, t, 0.18);
                tone(ctx, 1200, t + 0.3, 0.18);
                tone(ctx, 1200, t + 0.6, 0.18);
            } else {
                tone(ctx, 880, t, 0.2);
            }
            return true;
        } catch (e) {
            return false;
        }
    }

    window.cafeAlerts = {
        unlock: unlock,
        isUnlocked: isUnlocked,
        isMuted: isMuted,
        setMuted: setMuted,
        beep: beep
    };
})();
