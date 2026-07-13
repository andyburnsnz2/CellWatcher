#pragma once

// LilyGO T-CAN485 pin mapping — taken from LilyGO's own reference project
// (Xinyuan-LilyGO/T-CAN485, lib/Mylibrary/pin_config.h), not guessed. RS485/SD pins are omitted
// since this firmware doesn't use them.

// CAN transceiver (SN65HVD231) data lines, wired to the ESP32's built-in TWAI controller.
#define CAN_TX_PIN 27
#define CAN_RX_PIN 26

// SN65HVD231 speed-select pin — LOW selects high-speed mode (required; the transceiver defaults
// to a slower slope-controlled mode otherwise, which is not what we want for CAN sniffing).
#define CAN_SPEED_MODE_PIN 23

// Onboard boost regulator (ME2107A50M5G) that powers the CAN/RS485 transceivers — must be driven
// HIGH or the transceiver has no supply at all and nothing will be received.
#define BOOST_ENABLE_PIN 16

// Onboard WS2812 RGB status LED — driven with FastLED (see main.cpp's heartbeatLoop), same
// library/pin LilyGO's own WS2812B_Blink reference example for this board uses.
#define STATUS_LED_PIN 4
