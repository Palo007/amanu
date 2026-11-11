"""Candidate removers built from solve.py and denoise.py, each (mic, far) -> cleaned mic."""
import numpy as np
import solve, denoise

import variants


def solver(floor=None, over=2.0):
    """The offline canceller; with `floor`, also its residual suppressor. A recording that
    joins many devices goes to plain Sonora, as the delay-primed Sonora models do."""
    def run(m, f):
        out, info = solve.cancel(m, f)
        if info["many_paths"]:
            return variants.VARIANTS["sonora"](m, f)
        return solve.suppress(out, info["echo"], floor, over) if floor and info["sections"] else out
    return run


VARIANTS = {
    "solve": solver(),
    "solve-res15": solver(0.15),
    "solve-res30": solver(0.30),
}


def then(first, second):
    return lambda m, f: second(first(m, f))


VARIANTS.update({
    "solve+dn": then(VARIANTS["solve"], denoise.dpdf),
    "solve-res15+dn": then(VARIANTS["solve-res15"], denoise.dpdf),
    "sdf+dn": then(variants.VARIANTS["bw60-floor50"], denoise.dpdf),     # today's best, then the denoiser
})
