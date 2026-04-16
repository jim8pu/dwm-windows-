# Comprehensive Strategies for the f.lux Interference Bug

The core issue is that your Tiling Window Manager (TWM) broadcasts system-wide `SPIF_SENDCHANGE` signals to toggle the OS-native Focus Follows Mouse (XMouse) feature off and on during popup events (like toast notifications). These broadcasts interrupt f.lux's gamma ramp.

Below is an exhaustive conceptual breakdown of all possible ways to fix this, along with how each approach impacts other known issues (like the "Taskbar Bug" and the "Flow Launcher Bug").

---

## Strategy 1: The "Silent Toggle" (Modify the Broadcast Flag)

**Concept:** 
Continue turning the OS XMouse feature OFF during popups and back ON afterward, exactly as your TWM does now. However, change the system parameter flag from `SPIF_SENDCHANGE` to `0`. This triggers the OS feature toggle silently within the Windows kernel without broadcasting a `WM_SETTINGCHANGE` message to every app on your system.

* **Pros:**
  * **Easiest implementation:** Requires changing exactly one character of code in one function.
  * **Fixes f.lux:** f.lux never receives the broadcast spam, so the screen gamma remains stable.
  * **Retains Popup Pausing:** If you open Flow Launcher, XMouse gets disabled successfully. You can move your mouse around without the background window stealing your focus, keeping your typing safe.
* **Cons:**
  * **Fails to fix the Taskbar Bug:** When you hover over the Windows Taskbar, XMouse turns OFF. When you slide your mouse *off* the taskbar back onto your browser, XMouse is dead. Focus won't follow your mouse until you manually click to wake it back up.

---

## Strategy 2: The "Permanent OS XMouse" (Stop Toggling Entirely)

**Concept:**
Windows OS XMouse works perfectly fine if you just turn it ON and leave it ON. The solution here is to turn it ON when your TWM starts, and stop micro-managing it. Delete the code that disables XMouse when popups or untiled windows appear.

* **Pros:**
  * **Fixes f.lux entirely:** Because you never toggle the setting during runtime, `SPIF_SENDCHANGE` is never fired.
  * **Fixes the Taskbar Bug:** If you hover over the taskbar, XMouse stays ON. Moving off the taskbar instantly focuses the tiled window underneath it.
* **Cons:**
  * **Introduces the "Flow Launcher Bug":** The OS-native XMouse is a blunt tool—it aggressively focuses *everything* your mouse touches. If you open Flow Launcher (or an Alt-Tab menu) and accidentally bump your mouse 1 pixel onto a tiled window in the background, XMouse will instantly steal focus back to the tiled window, interrupting your typing and potentially dismissing your popup.

---

## Strategy 3: The "Exclusion List" (Filter Specific Popups)

**Concept:**
Keep the popup-toggling logic exactly as it is (including fixing the flag to `0`), but maintain a hardcoded list of window classes that are "safe" (e.g., Windows Toasts, Wi-Fi menus). Only turn XMouse OFF if the active popup is NOT on the safe list.

* **Pros:**
  * Retains the helpful popup-pausing behavior for apps like Flow Launcher (which aren't on the list).
* **Cons:**
  * **Impossible to maintain:** Every custom app (Discord, Telegram, Slack, Electron apps) uses completely different, undocumented window classes for their notifications. Your TWM will constantly misidentify notifications, leading to the same bugs reoccurring.
  * **Doesn't natively solve the Taskbar Bug** without adding extremely complex edge-case logic that turns your codebase into spaghetti. 

---

## Strategy 4: The Custom Context-Aware "Mouse Tracker" (The Komorebi Approach)

**Concept:**
Abandon the unreliable Windows native OS XMouse feature completely. Instead, build a lightweight background thread in your TWM that checks the physical mouse position 20 times a second. By reading the window underneath the mouse cursor, your code gets to systematically decide when it is appropriate to steal focus and when it should gracefully pause.

* **Pros:**
  * **Fixes f.lux 100%:** You never touch `SystemParametersInfoW`, so the OS is entirely bypassed. No broadcasts are ever sent.
  * **Fixes Flow Launcher (Pausing) 100%:** You can explicitly program the tracker: _"If the active window is a popup, pause the tracking loop immediately."_ Your mouse can move anywhere without stealing focus.
  * **Fixes the Taskbar Bug 100%:** You can explicitly program the tracker: _"If the mouse is hovering over the Taskbar, ignore it."_ This prevents the taskbar from inappropriately stealing focus and locking up your workflow.
  * **Perfect Customization:** If you build your own Custom Taskbar in the future, you can easily instruct the Custom Mouse Tracker to interact with it exactly how you want.
* **Cons:**
  * Requires writing and maintaining a custom tracking class (though it's only ~100 lines of C#).
  * Technically requires minimal CPU polling overhead (though waking a thread every 50ms consumes less than 0.01% of a standard CPU, which is virtually unmeasurable).
