import { loadFont } from "@remotion/google-fonts/Poppins";

// Clean, modern font for the finance channel (replaces Arial Black / Segoe UI).
const { fontFamily } = loadFont("normal", { weights: ["400", "500", "600", "700", "800"] });

export const FIN_FONT = fontFamily;
