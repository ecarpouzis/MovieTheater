import { useState, useEffect } from "react";

function useTouchDevice() {
  const [isTouch, setIsTouch] = useState(() => window.matchMedia("(hover: none)").matches);
  useEffect(() => {
    const mq = window.matchMedia("(hover: none)");
    const handler = (e) => setIsTouch(e.matches);
    mq.addEventListener("change", handler);
    return () => mq.removeEventListener("change", handler);
  }, []);
  return isTouch;
}

export default useTouchDevice;
