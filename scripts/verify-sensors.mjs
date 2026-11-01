import assert from "node:assert/strict";
import {
  createSimulatedGeo,
  createSimulatedMotion,
  createSimulatedOrientation,
  formatNumber,
} from "../src/lib/sensors.ts";

const motion = createSimulatedMotion(0);
assert.equal(typeof motion.acceleration.x, "number");
assert.equal(typeof motion.accelerationIncludingGravity.z, "number");
assert.ok(motion.accelerationIncludingGravity.z > 9);

const orientation = createSimulatedOrientation(1200);
assert.ok(orientation.alpha >= 0 && orientation.alpha < 360);
assert.ok(Math.abs(orientation.beta) <= 35);
assert.ok(Math.abs(orientation.gamma) <= 25);

const geo = createSimulatedGeo(0);
assert.ok(geo.latitude < -36 && geo.latitude > -37);
assert.ok(geo.longitude > 174 && geo.longitude < 175);
assert.ok(geo.accuracy > 0);

assert.equal(formatNumber(null), "—");
assert.equal(formatNumber(1.2345, 2), "1.23");
assert.equal(formatNumber(Number.NaN), "—");

console.log("verify-sensors: all assertions passed");
