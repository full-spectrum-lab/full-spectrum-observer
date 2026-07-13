#!/usr/bin/env python3
"""
Full Spectrum Engine - awareness bomb engine.

L5 "awareness bomb" is a defensive soft-reset mechanism. It does not model a
production protocol network. In the local engine it simply:

- detects repeated purity / rigidity failures
- softens the state back into a feasible band
- records why the reset path was triggered
"""

from dataclasses import dataclass
from enum import Enum
from typing import Optional, Tuple
import time

from ..core.state import CivilizationState, project_to_feasible


class BombStage(Enum):
    IDLE = "IDLE"
    DETONATED = "DETONATED"
    QUANTUM_SUPERPOSITION = "QUANTUM_SUPERPOSITION"
    RECOVERY = "RECOVERY"
    NEW_EQUILIBRIUM = "NEW_EQUILIBRIUM"


@dataclass
class AwarenessBombState:
    stage: BombStage = BombStage.IDLE
    trigger_count: int = 0
    detonation_time: Optional[float] = None
    recovery_progress: float = 0.0
    trigger_reason: str = ""


class AwarenessBombEngine:
    """Soft-reset controller for rigid or repeatedly failing states."""

    MAX_CONSECUTIVE_FAILURES = 3
    RECOVERY_DURATION = 10.0
    RIGIDITY_THRESHOLD = 0.85

    def __init__(self) -> None:
        self.state = AwarenessBombState()
        self._failure_counter = 0

    def check_trigger(
        self,
        S: CivilizationState,
        purity: float,
        rigidity: float,
    ) -> Tuple[bool, str]:
        """
        Check whether the awareness-bomb path should trigger.

        Returns:
            (triggered, reason)
        """
        if purity < 0.7:
            self._failure_counter += 1
            if self._failure_counter >= self.MAX_CONSECUTIVE_FAILURES:
                return True, f"Purity below threshold for {self._failure_counter} consecutive checks"
        else:
            self._failure_counter = 0

        if rigidity > self.RIGIDITY_THRESHOLD:
            return True, f"Rigidity index {rigidity:.3f} exceeded threshold {self.RIGIDITY_THRESHOLD:.2f}"

        if S.survival < 0.2 and purity >= 0.7:
            return True, f"Survival layer dropped below emergency threshold: {S.survival:.3f}"

        return False, "not triggered"

    def detonate(self, S: CivilizationState, reason: str = "") -> CivilizationState:
        """Trigger a soft reset and project the state back to a safer band."""
        self.state.stage = BombStage.DETONATED
        self.state.detonation_time = time.time()
        self.state.trigger_count += 1
        self.state.trigger_reason = reason

        projected = project_to_feasible(S)
        softened = CivilizationState(
            survival=projected.survival,
            coordination=min(0.6, projected.coordination),
            meaning=max(0.3, projected.meaning),
        )
        return softened

    def recover(self, S: CivilizationState, step: int) -> Tuple[CivilizationState, BombStage]:
        """Advance recovery state after detonation."""
        if self.state.stage == BombStage.DETONATED:
            self.state.stage = BombStage.QUANTUM_SUPERPOSITION
            return S, self.state.stage

        if self.state.stage == BombStage.QUANTUM_SUPERPOSITION:
            self.state.recovery_progress = min(1.0, step / self.RECOVERY_DURATION)
            if self.state.recovery_progress >= 1.0:
                self.state.stage = BombStage.NEW_EQUILIBRIUM
            return S, self.state.stage

        return S, self.state.stage

    def reset(self) -> None:
        """Reset controller state."""
        self.state = AwarenessBombState()
        self._failure_counter = 0
