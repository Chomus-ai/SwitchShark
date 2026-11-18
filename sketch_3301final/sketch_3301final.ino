// =====================================================================
// sketch_3301CONTROLLER.ino   (quaternion heading + self-configuring IMU
//                              + slow-top-up airbag + restart command)
// Merges sketch_3301IMU.ino (JY901S steering) and sketch_3301AIRBAG.ino
// (pressure sensor + pump "button") into ONE sketch / ONE USB cable.
//
// OUTPUT  (USB serial, 115200 baud, one line every 20 ms):
//   D,<yawDeg>,<pitchDeg>,<rollDeg>,<squeezing>,<imuReady>,<motionDps>
//     yawDeg      heading of the controller's NOSE, relative to the neutral pose
//     pitchDeg    nose elevation relative to neutral (positive = nose up)
//     rollDeg     sideways tilt relative to neutral (gravity-referenced, no drift)
//     squeezing   1 while the airbag is being squeezed, else 0
//     imuReady    0 while calibrating the neutral pose, 1 once ready
//     motionDps   how much the controller is moving (deg/s, over ~200 ms)
//   Lines starting with '#' are human-readable status messages.
//   "# BOOT" is printed once every time the board (re)starts. Unity uses it to
//   tell whether opening the port reset the board.
//
// INPUT   (from Unity, single characters)
//   'C' -> restart neutral-pose calibration only
//   'R' -> restart the whole session: re-calibrate the IMU AND re-inflate /
//          re-settle the airbag (replaces the old "port open resets the board"
//          behaviour, which is not reliable on every OS)
//
// AIRBAG:  the bag leaks slowly, so the pump gives tiny, rate-limited "doses"
//          whenever the pressure sags below the learned target. There is no
//          pump pulse after each squeeze any more.
//
// WIRING CHANGE: JY901S RX <- Arduino D3 (needed once, to configure the IMU).
//
// FIRST-TIME SETUP: set CONFIGURE_IMU_ON_BOOT = true, upload, wait for
// "# IMU CONFIG SAVED", then set it back to false and upload again.
// =====================================================================

#include <SoftwareSerial.h>
#include <math.h>
#include <string.h>

// ---------- PINS ----------
SoftwareSerial imu(2, 3);          // D2 <- JY901S TX, D3 -> JY901S RX

const int enA = 9;
const int in1 = 4;
const int in2 = 5;
const int STBY = 10;
const int pressurePin = A0;

// ---------- IMU SETTINGS ----------
const bool CONFIGURE_IMU_ON_BOOT = false;  // true for ONE upload to program the module
const bool USE_6AXIS = false;               // true = gyro+accel only (no magnetometer)

// Which sensor axis points along the controller's nose: 0 = X, 1 = Y, 2 = Z.
// The old roll/pitch swap suggests Y. Verify with the checks in the notes.
const int FORWARD_AXIS = 1;

// ---------- OUTPUT TIMING ----------
const unsigned long SEND_INTERVAL = 20;      // ms between D lines (50 Hz)
unsigned long lastSend = 0;

// =====================================================================
// IMU
// =====================================================================
uint8_t packet[11];
int packetIndex = 0;
unsigned long goodPackets = 0;
unsigned long badPackets = 0;
unsigned long lastQuatTime = 0;
unsigned long lastStatTime = 0;
unsigned long typeCount[16];   // index = packet type - 0x50

float roll = 0;
float pitch = 0;
float yaw = 0;

// Starting orientation
float neutralPitch = 0;
float neutralYaw = 0;
float neutralRoll = 0;

bool calibrated = false;
int calibrationSamples = 0;
const int CALIBRATION_SAMPLES = 50;        // ~1 s at 50 Hz
const unsigned long WARMUP_MS = 4000;      // let the module's filter settle after power-up
const float STEADY_TOLERANCE = 2.0;        // degrees of wander allowed while calibrating

float pitchTotal = 0;
float yawSinTotal = 0;
float yawCosTotal = 0;
float rollSinTotal = 0;
float rollCosTotal = 0;

// Angles relative to the neutral pose (what gets sent to Unity)
float relYaw = 0;
float relPitch = 0;
float relRoll = 0;

// Motion estimate for Unity's "hold still" check
const unsigned long MOTION_WINDOW = 200;   // ms
unsigned long lastMotionSample = 0;
bool motionPrimed = false;
float prevYaw = 0;
float prevPitch = 0;
float prevRoll = 0;
float motionDps = 0;

// Handle angle wraparound at +/-180 degrees
float angleDifference(float current, float reference) {
  float diff = current - reference;
  while (diff > 180) diff -= 360;
  while (diff < -180) diff += 360;
  return diff;
}

void startCalibration() {
  calibrated = false;
  calibrationSamples = 0;
  pitchTotal = 0;
  yawSinTotal = 0;
  yawCosTotal = 0;
  rollSinTotal = 0;
  rollCosTotal = 0;
  relYaw = 0;
  relPitch = 0;
  relRoll = 0;
  motionPrimed = false;
  motionDps = 0;
  Serial.println(F("# IMU CALIBRATING - hold controller level and forward"));
}

// ---------- Program the JY901S (settings are saved inside the module) ----------
void imuCommand(uint8_t reg, uint8_t lo, uint8_t hi) {
  imu.write((uint8_t)0xFF);
  imu.write((uint8_t)0xAA);
  imu.write(reg);
  imu.write(lo);
  imu.write(hi);
  delay(200);                       // the module needs a pause between commands
}

void configureImu() {
  Serial.println(F("# IMU CONFIG: writing settings..."));
  imuCommand(0x69, 0x88, 0xB5);                 // unlock
  imuCommand(0x02, 0x00, 0x02);                 // RSW: output quaternion only (bit 9)
  imuCommand(0x03, 0x08, 0x00);                 // RRATE: 50 Hz
  imuCommand(0x24, USE_6AXIS ? 1 : 0, 0x00);    // algorithm: 1 = 6-axis, 0 = 9-axis
  imuCommand(0x00, 0x00, 0x00);                 // save
  Serial.println(F("# IMU CONFIG SAVED - set CONFIGURE_IMU_ON_BOOT = false and re-upload"));
}

// ---------- Packet framing with resync ----------
// Returns true when packet[] holds a verified 11-byte frame.
bool feedByte(uint8_t b) {
  if (packetIndex == 0 && b != 0x55) return false;
  packet[packetIndex++] = b;
  if (packetIndex < 11) return false;

  uint8_t sum = 0;
  for (int i = 0; i < 10; i++) sum += packet[i];
  if (sum == packet[10]) {
    packetIndex = 0;
    goodPackets++;
    return true;
  }

  // Bad frame: slide forward to the next 0x55 inside it instead of dropping all 11 bytes
  badPackets++;
  int next = -1;
  for (int i = 1; i < 11; i++) {
    if (packet[i] == 0x55) { next = i; break; }
  }
  if (next < 0) { packetIndex = 0; return false; }
  int remaining = 11 - next;
  memmove(packet, packet + next, remaining);
  packetIndex = remaining;
  return false;
}

// Direction of one sensor axis (0=X, 1=Y, 2=Z) expressed in world coordinates
void axisInWorld(int axis, float q0, float q1, float q2, float q3,
                 float &x, float &y, float &z) {
  if (axis == 0) {
    x = q0*q0 + q1*q1 - q2*q2 - q3*q3;
    y = 2 * (q1*q2 + q0*q3);
    z = 2 * (q1*q3 - q0*q2);
  } else if (axis == 1) {
    x = 2 * (q1*q2 - q0*q3);
    y = q0*q0 - q1*q1 + q2*q2 - q3*q3;
    z = 2 * (q2*q3 + q0*q1);
  } else {
    x = 2 * (q1*q3 + q0*q2);
    y = 2 * (q2*q3 - q0*q1);
    z = q0*q0 - q1*q1 - q2*q2 + q3*q3;
  }
}

void readImu() {
  while (imu.available()) {
    if (!feedByte(imu.read())) continue;

    uint8_t t = packet[1] - 0x50;               // per-type packet counters (for debugging)
    if (t < 16) typeCount[t]++;

    if (packet[1] != 0x59) continue;            // 0x59 = quaternion packet
    lastQuatTime = millis();

    float q0 = (int16_t)((packet[3] << 8) | packet[2]) / 32768.0;
    float q1 = (int16_t)((packet[5] << 8) | packet[4]) / 32768.0;
    float q2 = (int16_t)((packet[7] << 8) | packet[6]) / 32768.0;
    float q3 = (int16_t)((packet[9] << 8) | packet[8]) / 32768.0;

    float norm = sqrt(q0*q0 + q1*q1 + q2*q2 + q3*q3);
    if (norm < 0.5) continue;                   // garbage frame
    q0 /= norm; q1 /= norm; q2 /= norm; q3 /= norm;

    // Nose direction and the sideways axis, in world coordinates
    float fx, fy, fz, sx, sy, sz, ux, uy, uz;
    axisInWorld(FORWARD_AXIS, q0, q1, q2, q3, fx, fy, fz);
    axisInWorld((FORWARD_AXIS + 2) % 3, q0, q1, q2, q3, sx, sy, sz);
    axisInWorld((FORWARD_AXIS + 1) % 3, q0, q1, q2, q3, ux, uy, uz);

    // Heading of the nose (hold the last value if it points almost straight up/down)
    if (sqrt(fx*fx + fy*fy) > 0.2) {
      yaw = atan2(fy, fx) * 180.0 / PI;
    }
    pitch = asin(constrain(fz, -1.0, 1.0)) * 180.0 / PI;   // nose up = positive
    roll = atan2(sz, uz) * 180.0 / PI;  // Sideways tilt, full +/-180 range (continuous through belly-up)

    // Calibrate neutral orientation (only counts while warmed up and held steady)
    if (!calibrated) {
      static float refY = 0, refP = 0, refR = 0;
      if (calibrationSamples == 0) { refY = yaw; refP = pitch; refR = roll; }

      bool steady = fabs(angleDifference(yaw,   refY)) < STEADY_TOLERANCE &&
                    fabs(angleDifference(pitch, refP)) < STEADY_TOLERANCE &&
                    fabs(angleDifference(roll,  refR)) < STEADY_TOLERANCE;

      if (millis() < WARMUP_MS || !steady) {    // restart the window
        calibrationSamples = 0;
        pitchTotal = 0;
        yawSinTotal = yawCosTotal = 0;
        rollSinTotal = rollCosTotal = 0;
        continue;
      }

      pitchTotal += pitch;
      float yawRad = yaw * PI / 180.0;
      yawSinTotal += sin(yawRad);
      yawCosTotal += cos(yawRad);
      float rollRad = roll * PI / 180.0;
      rollSinTotal += sin(rollRad);
      rollCosTotal += cos(rollRad);
      calibrationSamples++;

      if (calibrationSamples >= CALIBRATION_SAMPLES) {
        neutralPitch = pitchTotal / CALIBRATION_SAMPLES;
        neutralYaw = atan2(yawSinTotal, yawCosTotal) * 180.0 / PI;
        neutralRoll = atan2(rollSinTotal, rollCosTotal) * 180.0 / PI;
        calibrated = true;
        Serial.println(F("# IMU CALIBRATION COMPLETE"));
      }
      continue;
    }

    // Relative orientation (sent to Unity as-is)
    relYaw = angleDifference(yaw, neutralYaw);
    relPitch = pitch - neutralPitch;
    relRoll = angleDifference(roll, neutralRoll);
  }
}

// Called every loop: how much did the controller move over the last
// MOTION_WINDOW ms? (sum of the three angle changes, in degrees per second)
void updateMotion(unsigned long now) {
  if (!calibrated) {
    motionPrimed = false;
    motionDps = 0;
    return;
  }

  if (!motionPrimed) {
    prevYaw = yaw;
    prevPitch = pitch;
    prevRoll = roll;
    lastMotionSample = now;
    motionPrimed = true;
    return;
  }

  if (now - lastMotionSample < MOTION_WINDOW) return;

  float dt = (now - lastMotionSample) / 1000.0;
  float delta = fabs(angleDifference(yaw, prevYaw))
              + fabs(angleDifference(pitch, prevPitch))
              + fabs(angleDifference(roll, prevRoll));
  motionDps = delta / dt;

  prevYaw = yaw;
  prevPitch = pitch;
  prevRoll = roll;
  lastMotionSample = now;
}

// Status for debugging: packet counts every 5 s, plus a warning if no data arrives
void reportImuStatus(unsigned long now) {
  if (now - lastStatTime < 5000) return;
  lastStatTime = now;
  Serial.print(F("# imu good="));
  Serial.print(goodPackets);
  Serial.print(F(" bad="));
  Serial.println(badPackets);
  for (int i = 0; i < 16; i++) {
    if (typeCount[i] > 0) {
      Serial.print(F("#   type 0x")); Serial.print(0x50 + i, HEX);
      Serial.print(F(" = ")); Serial.println(typeCount[i]);
    }
  }
  if (now > 3000 && (lastQuatTime == 0 || (long)(now - lastQuatTime) > 2000)) {
    Serial.println(F("# NO QUATERNION DATA - check D3->RX wiring, run the one-time config"));
  }
}

// =====================================================================
// AIRBAG - squeeze = action button, with slow top-up against leakage
// =====================================================================
const int INITIAL_TARGET = 150;     // inflate to about this many ADC counts at start
const int MAX_PRESSURE = 170;       // hard ceiling (pump stops above this)

// Squeeze detection, relative to the (slowly drifting) baseline
const int SQUEEZE_THRESHOLD = 4;
const int RELEASE_THRESHOLD = 2;

// Pump
const int PUMP_PWM = 255;                       // keep full: small DC pumps stall at low PWM
const unsigned long MAX_INITIAL_TIME = 2500;    // give up on the first inflation after this

// Slow top-up. The "dose" is set by pulse LENGTH, not by PWM.
const int   TOPUP_DEADBAND = 2;                 // dose when this many counts below target
const unsigned long TOPUP_PULSE = 150;           // ms of pumping per dose  (tune: 30-100)
const unsigned long TOPUP_SETTLE = 100;         // ms to let pressure settle after a dose
const unsigned long TOPUP_MIN_GAP = 400;       // ms between doses (this is what makes it "slow")
const unsigned long POST_SQUEEZE_QUIET = 300;   // no dosing right after a squeeze
const float TOPUP_MIN_GAIN = 1.0;               // a dose should add at least this many counts
const int   TOPUP_MAX_WEAK = 20;                 // this many weak doses in a row = pause + warn
const unsigned long TOPUP_LOCKOUT = 30000;      // ms to pause dosing after that
const float BASELINE_TRACK = 0.002;             // per 10 ms tick, about a 5 s time constant

// Timing
const unsigned long DEBOUNCE = 50;
const unsigned long SETTLE_TIME = 600;

// How far below MAX_PRESSURE the pressure must fall before the airbag
// recovers from FINISHED (prevents a hard squeeze killing the button)
const int FINISHED_RECOVERY_MARGIN = 10;

// The filter and thresholds were tuned for a ~10 ms loop, so the airbag logic keeps that cadence.
const unsigned long AIRBAG_INTERVAL = 10;
unsigned long lastAirbagUpdate = 0;

float filteredPressure = 0;
float baseline = 0;
float targetPressure = 0;
bool targetSet = false;

int squeezeCount = 0;

bool squeezing = false;
bool pumpRunning = false;

unsigned long squeezeStart = 0;
unsigned long releaseStart = 0;
unsigned long pumpStart = 0;
unsigned long settleStart = 0;
unsigned long settleDuration = SETTLE_TIME;
unsigned long lastDoseTime = 0;
unsigned long lastSqueezeEnd = 0;
unsigned long topupBlockedUntil = 0;
float pressureBeforeDose = 0;
bool dosePending = false;
int weakDoses = 0;

enum State {
  INITIAL_INFLATION,
  SETTLING,
  READY,
  PUMPING,
  FINISHED
};

State state = INITIAL_INFLATION;
State afterSettling = READY;

void pumpOn() {
  digitalWrite(in1, HIGH);
  digitalWrite(in2, LOW);
  digitalWrite(STBY, HIGH);
  analogWrite(enA, PUMP_PWM);
  pumpRunning = true;
}

void pumpOff() {
  analogWrite(enA, 0);
  digitalWrite(in1, LOW);
  digitalWrite(in2, LOW);
  digitalWrite(STBY, LOW);
  pumpRunning = false;
}

// Reset all airbag state and begin (or skip, if already inflated) the initial inflation.
// Used by setup() and by the 'R' command.
void startAirbagSession() {
  pumpOff();
  squeezing = false;
  squeezeStart = 0;
  releaseStart = 0;
  squeezeCount = 0;
  dosePending = false;
  weakDoses = 0;
  topupBlockedUntil = 0;
  targetSet = false;               // target is re-learned after the next settle
  settleDuration = SETTLE_TIME;
  afterSettling = READY;

  if (filteredPressure >= INITIAL_TARGET) {
    // Already inflated (e.g. a mid-session restart): just settle.
    state = SETTLING;
    settleStart = millis();
  } else {
    state = INITIAL_INFLATION;
    pumpStart = millis();
    pumpOn();
    Serial.println(F("# INITIAL INFLATION"));
  }
}

void updateAirbag(unsigned long now) {
  int raw = analogRead(pressurePin);

  filteredPressure = 0.2 * raw + 0.8 * filteredPressure;

  // Stop pumping at configured ceiling (only announce/transition once)
  if (filteredPressure >= MAX_PRESSURE && state != FINISHED) {
    pumpOff();
    state = FINISHED;
    Serial.println(F("# PRESSURE LIMIT REACHED"));
  }

  switch (state) {

    // 1. Initial inflation
    case INITIAL_INFLATION:
      if (filteredPressure >= INITIAL_TARGET ||
          now - pumpStart >= MAX_INITIAL_TIME) {
        pumpOff();
        state = SETTLING;
        afterSettling = READY;
        settleDuration = SETTLE_TIME;
        settleStart = now;
        Serial.println(F("# INITIAL PUMP STOPPED"));
      }
      break;

    // 2. Allow pressure to stabilize
    case SETTLING:
      if (now - settleStart >= settleDuration) {
        baseline = filteredPressure;
        squeezing = false;
        squeezeStart = 0;
        releaseStart = 0;

        // The first settle after (re)start defines the pressure we maintain.
        if (!targetSet) {
          targetPressure = filteredPressure;
          targetSet = true;
          Serial.print(F("# TARGET PRESSURE "));
          Serial.println(targetPressure, 1);
        }

        // Did the last top-up dose actually raise the pressure?
        bool wasDose = dosePending;
        if (dosePending) {
          dosePending = false;
          float gain = filteredPressure - pressureBeforeDose;
          Serial.print(F("# dose gain="));
          Serial.println(gain, 1);
          if (gain < TOPUP_MIN_GAIN) {
            if (++weakDoses >= TOPUP_MAX_WEAK) {
              weakDoses = 0;
              topupBlockedUntil = now + TOPUP_LOCKOUT;
              Serial.println(F("# TOPUP PAUSED: doses add no pressure (big leak or pump fault?)"));
            }
          } else {
            weakDoses = 0;
          }
        }

        state = afterSettling;

        if (state == READY && !wasDose) {
          Serial.println(F("# READY TO SQUEEZE"));
        }
      }
      break;

    // 3. Detect squeeze and release; top up slowly while idle
    case READY: {
      float change = filteredPressure - baseline;

      if (!squeezing) {
        if (change >= SQUEEZE_THRESHOLD) {
          if (squeezeStart == 0) {
            squeezeStart = now;
          }
          if (now - squeezeStart >= DEBOUNCE) {
            squeezing = true;       // <- Unity sees this as "button down"
            releaseStart = 0;
          }
        } else {
          squeezeStart = 0;

          // Idle: let the baseline follow slow leakage, so squeeze detection
          // stays consistent as the bag deflates between doses.
          baseline += BASELINE_TRACK * (filteredPressure - baseline);

          // Slow top-up: one small dose when the bag has sagged below target.
          if (now >= topupBlockedUntil &&
              now - lastDoseTime >= TOPUP_MIN_GAP &&
              now - lastSqueezeEnd >= POST_SQUEEZE_QUIET &&
              filteredPressure < targetPressure - TOPUP_DEADBAND &&
              filteredPressure < MAX_PRESSURE - FINISHED_RECOVERY_MARGIN) {
            pressureBeforeDose = filteredPressure;
            dosePending = true;
            lastDoseTime = now;
            pumpStart = now;
            pumpOn();
            state = PUMPING;
          }
        }

      } else {
        if (change <= RELEASE_THRESHOLD) {
          if (releaseStart == 0) {
            releaseStart = now;
          }
          if (now - releaseStart >= DEBOUNCE) {
            squeezeCount++;

            squeezing = false;      // <- Unity sees this as "button up"
            squeezeStart = 0;
            releaseStart = 0;
            lastSqueezeEnd = now;
            // No pump pulse here any more: topping up is handled above.
          }
        } else {
          releaseStart = 0;
        }
      }
      break;
    }

    // 4. Brief pump dose
    case PUMPING:
      if (now - pumpStart >= TOPUP_PULSE) {
        pumpOff();
        state = SETTLING;
        afterSettling = READY;
        settleDuration = TOPUP_SETTLE;
        settleStart = now;
      }
      break;

    // 5. Maximum pressure reached
    case FINISHED:
      pumpOff();

      // Recover once pressure falls back down, instead of staying
      // locked out until the Arduino is reset.
      if (filteredPressure < MAX_PRESSURE - FINISHED_RECOVERY_MARGIN) {
        squeezing = false;
        squeezeStart = 0;
        releaseStart = 0;
        state = SETTLING;
        afterSettling = READY;
        settleDuration = SETTLE_TIME;
        settleStart = now;
        Serial.println(F("# RECOVERED FROM PRESSURE LIMIT"));
      }
      break;
  }
}

// =====================================================================
// SERIAL LINK TO UNITY
// =====================================================================

// Full session restart: IMU re-calibration + airbag re-inflate/settle.
// Hold the controller still for ~1-2 s afterwards.
void restartSession() {
  startCalibration();
  startAirbagSession();
  Serial.println(F("# SESSION RESTART"));
}

void readCommands() {
  while (Serial.available()) {
    char c = Serial.read();
    if (c == 'C') {
      startCalibration();
    } else if (c == 'R') {
      restartSession();
    }
  }
}

void sendFrame(unsigned long now) {
  if (now - lastSend < SEND_INTERVAL) return;
  lastSend = now;

  Serial.print(F("D,"));
  Serial.print(relYaw, 1);
  Serial.print(',');
  Serial.print(relPitch, 1);
  Serial.print(',');
  Serial.print(relRoll, 1);
  Serial.print(',');
  Serial.print(squeezing ? 1 : 0);
  Serial.print(',');
  Serial.print(calibrated ? 1 : 0);
  Serial.print(',');
  Serial.println(motionDps, 1);
}

// =====================================================================
// SETUP / LOOP
// =====================================================================
void setup() {
  Serial.begin(115200);   // USB link to Unity (must match Unity's Baud Rate)
  imu.begin(9600);        // JY901S stays at 9600 (quaternion-only 50 Hz fits easily)

  // Unity watches for this to learn that the board rebooted by itself.
  Serial.println(F("# BOOT"));

  pinMode(enA, OUTPUT);
  pinMode(in1, OUTPUT);
  pinMode(in2, OUTPUT);
  pinMode(STBY, OUTPUT);
  pinMode(pressurePin, INPUT);

  pumpOff();

  if (CONFIGURE_IMU_ON_BOOT) {
    configureImu();
  }

  Serial.println(F("# Hold controller level and forward"));

  // Initial sensor reading
  long total = 0;
  for (int i = 0; i < 100; i++) {
    total += analogRead(pressurePin);
    delay(10);
  }
  filteredPressure = total / 100.0;
  baseline = filteredPressure;

  // Bytes that arrived during the delays above may be a partial packet
  packetIndex = 0;

  // Begin initial inflation (IMU calibration starts by itself: calibrated == false)
  startAirbagSession();
}

void loop() {
  unsigned long now = millis();

  readCommands();
  readImu();                       // every pass: keeps the 64-byte buffer drained
  updateMotion(now);

  if (now - lastAirbagUpdate >= AIRBAG_INTERVAL) {
    lastAirbagUpdate = now;
    updateAirbag(now);
  }

  reportImuStatus(now);
  sendFrame(now);
  // No delay() here: SoftwareSerial needs frequent reads to avoid overflow.
}
