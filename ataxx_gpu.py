"""Low-overhead GPU telemetry for the trainer (NVML via the optional `nvidia-ml-py` package).

One background daemon thread wakes every `interval` seconds, does four NVML reads (~1 ms total) and sleeps; nothing
runs on the training loop. If pynvml or a GPU is missing, or any read fails, the monitor disables itself and the run
continues unchanged. Default interval 0 = no thread is even started.

Two different memory numbers are reported, on purpose:
  * NVML "used"            whole-GPU memory, including the desktop, browsers and other processes (see the baseline)
  * torch peak allocated   this process's own tensors; torch peak reserved = what PyTorch's allocator holds from the driver
Use torch peak reserved vs. the GPU total to decide how much larger a batch or net would fit.
"""

import threading
import time


class GpuMonitor:
    def __init__(self, interval, index=0, nvml=None, log=print, label="train", print_every=6):
        self.interval = float(interval)
        self.label = label
        self.log = log
        self.print_every = max(1, int(print_every))
        self.enabled = False
        self.disabled_reason = None
        self.name = None
        self._nvml = None
        self._handle = None
        self._stop = threading.Event()
        self._thread = None
        self._lock = threading.Lock()
        self._reset_window()
        self._all = self._new_window()
        self._last = None
        self.total_mib = 0
        self.baseline_mib = 0
        if self.interval <= 0:
            self.disabled_reason = "interval is 0"
            return
        try:
            if nvml is None:
                import pynvml as nvml  # provided by the `nvidia-ml-py` package
            nvml.nvmlInit()
            self._nvml = nvml
            self._handle = nvml.nvmlDeviceGetHandleByIndex(index)
            name = nvml.nvmlDeviceGetName(self._handle)
            self.name = name.decode() if isinstance(name, bytes) else name
            mem = nvml.nvmlDeviceGetMemoryInfo(self._handle)
            self.total_mib = mem.total // 1048576
            self.baseline_mib = mem.used // 1048576
            self.enabled = True
        except Exception as ex:  # missing package, no driver, no GPU: never fatal
            self.disabled_reason = f"{type(ex).__name__}: {ex}"
            self._shutdown()
            log(f"[gpu] {label}: telemetry disabled ({self.disabled_reason}); the run is unaffected")
            return
        log(f"[gpu] {label}: monitoring {self.name}, {self.total_mib} MiB total, {self.baseline_mib} MiB already in use, every {self.interval:g}s")
        self._thread = threading.Thread(target=self._loop, name="gpu-monitor", daemon=True)
        self._thread.start()

    @staticmethod
    def _new_window():
        return {"n": 0, "util_sum": 0.0, "util_max": 0, "idle": 0, "mem_peak": 0, "temp_max": 0, "power_sum": 0.0, "power_max": 0.0}

    def _reset_window(self):
        self._window = self._new_window()

    def _sample(self):
        n = self._nvml
        util = n.nvmlDeviceGetUtilizationRates(self._handle).gpu
        mem = n.nvmlDeviceGetMemoryInfo(self._handle).used // 1048576
        try:
            temp = n.nvmlDeviceGetTemperature(self._handle, n.NVML_TEMPERATURE_GPU)
            power = n.nvmlDeviceGetPowerUsage(self._handle) / 1000.0
        except Exception:  # optional sensors
            temp, power = 0, 0.0
        with self._lock:
            for w in (self._window, self._all):
                w["n"] += 1
                w["util_sum"] += util
                w["util_max"] = max(w["util_max"], util)
                w["idle"] += 1 if util < 5 else 0
                w["mem_peak"] = max(w["mem_peak"], mem)
                w["temp_max"] = max(w["temp_max"], temp)
                w["power_sum"] += power
                w["power_max"] = max(w["power_max"], power)
            self._last = (util, mem, temp, power)

    def _loop(self):
        tick = 0
        while not self._stop.wait(self.interval):
            try:
                self._sample()
            except Exception as ex:
                self.enabled = False
                self.disabled_reason = f"NVML read failed: {ex}"
                return
            tick += 1
            if tick % self.print_every == 0:
                u, m, t, p = self._last
                self.log(f"[gpu] {self.label}: util {u}% | mem {m} / {self.total_mib} MiB (+{m - self.baseline_mib} vs start) | {t} C | {p:.0f} W")

    @staticmethod
    def _fmt(w):
        if w["n"] == 0:
            return None
        return {
            "samples": w["n"],
            "util_avg": round(w["util_sum"] / w["n"], 1),
            "util_max": w["util_max"],
            "idle_pct": round(100.0 * w["idle"] / w["n"], 1),
            "mem_peak_mib": w["mem_peak"],
            "temp_max": w["temp_max"],
            "power_avg_w": round(w["power_sum"] / w["n"], 1),
            "power_max_w": round(w["power_max"], 1),
        }

    def take_window(self):
        """Stats since the previous call (e.g. one epoch), then reset the window. None if nothing was sampled."""
        with self._lock:
            out = self._fmt(self._window)
            self._reset_window()
        return out

    def summary(self):
        with self._lock:
            out = self._fmt(self._all)
        if out is not None:
            out.update({"device": self.name, "total_mib": self.total_mib, "baseline_mib": self.baseline_mib})
        return out

    def close(self):
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2)
        s = self.summary()
        if s:
            self.log(f"[gpu] {self.label} summary: util avg {s['util_avg']}% max {s['util_max']}% (idle<5% in {s['idle_pct']}% of samples) | "
                     f"mem peak {s['mem_peak_mib']} / {s['total_mib']} MiB over a {s['baseline_mib']} MiB baseline | "
                     f"temp max {s['temp_max']} C | power avg {s['power_avg_w']} W max {s['power_max_w']} W")
        self._shutdown()

    def _shutdown(self):
        if self._nvml is not None:
            try:
                self._nvml.nvmlShutdown()
            except Exception:
                pass
            self._nvml = None


def torch_memory_stats(device):
    """This process's own GPU memory (free counter reads, no NVML). Empty on CPU."""
    import torch
    if device.type != "cuda":
        return {}
    return {
        "torch_peak_allocated_mib": torch.cuda.max_memory_allocated(device) // 1048576,
        "torch_peak_reserved_mib": torch.cuda.max_memory_reserved(device) // 1048576,
    }
