import { Config } from "@remotion/cli/config";

Config.setVideoImageFormat("jpeg");
Config.setOverwriteOutput(true);
// H.264 mp4 for broad playback (browsers, mobile, social).
Config.setCodec("h264");
