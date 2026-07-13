#!/usr/bin/env python3
"""
Full Spectrum Engine - causal chain report generator.

This module converts ESS results, FSHI state, and governance validation
signals into a stable, structured causal-chain artifact suitable for:

- golden sample regression
- local audit review
- explainability walkthroughs
- downstream protocol mapping
"""

import hashlib
import json
import time
from dataclasses import dataclass
from typing import Dict, List, Optional

from .bridge.runestone import Runestone
from .core.state import CivilizationState
from .engine.ess import ESSResult


@dataclass
class CausalChain:
    """Structured causal-chain report."""

    causal_chain_id: str
    timestamp: str
    version: str
    system_state: Dict
    ess_decision_context: Dict
    causal_paths: List[Dict]
    dream_butterfly_validation: Dict
    soul_account: Dict
    who_hurts: Dict
    frequency_plan: Dict
    full_chain_narrative: str


class ReportGenerator:
    """Build a deterministic, human-readable causal-chain report."""

    def __init__(self) -> None:
        self.version = "1.0"

    def generate(
        self,
        ess_result: ESSResult,
        dream_butterfly_result: Dict,
        state: CivilizationState,
        runestone: Runestone,
        selected_option: str,
        judgment_basis: List[str],
        system_state: str,
        spectrum_priority: str,
        timestamp: Optional[str] = None,
        causal_chain_id: Optional[str] = None,
    ) -> Dict:
        """Generate the full causal-chain report."""
        timestamp = timestamp or time.strftime("%Y-%m-%dT%H:%M:%SZ")

        if causal_chain_id is None:
            state_payload = json.dumps(
                {
                    "survival": state.survival,
                    "coordination": state.coordination,
                    "meaning": state.meaning,
                    "selected_option": selected_option,
                    "system_state": system_state,
                    "runestone_id": runestone.runestone_id,
                },
                sort_keys=True,
            )
            state_digest = hashlib.sha256(state_payload.encode("utf-8")).hexdigest()[:8]
            causal_chain_id = f"CC_{state_digest}"

        return {
            "causal_chain_id": causal_chain_id,
            "timestamp": timestamp,
            "version": self.version,
            "system_state": {
                "current_state": system_state,
                "judgment_basis": judgment_basis,
                "spectrum_priority": spectrum_priority,
            },
            "ess_decision_context": {
                "candidate_options": [p.option for p in ess_result.paths],
                "selected_option": selected_option,
                "horizon": ess_result.horizon,
            },
            "causal_paths": self._build_causal_paths(ess_result),
            "dream_butterfly_validation": dream_butterfly_result,
            "soul_account": self._build_soul_account(state),
            "who_hurts": self._build_who_hurts(state),
            "frequency_plan": self._build_frequency_plan(state, system_state, spectrum_priority),
            "full_chain_narrative": self._build_narrative(
                state=state,
                ess_result=ess_result,
                system_state=system_state,
                runestone=runestone,
            ),
        }

    def _build_causal_paths(self, ess_result: ESSResult) -> List[Dict]:
        paths: List[Dict] = []
        for path in ess_result.paths:
            paths.append(
                {
                    "path_id": path.option,
                    "option": path.option,
                    "selected": path.selected,
                    "frequency_impacts": {
                        "low_frequency": {
                            "signal_type": "survival_support"
                            if path.low_freq_impact < 0.5
                            else "survival_pressure",
                            "intensity": path.low_freq_impact,
                            "description": f"Low-frequency impact intensity {path.low_freq_impact:.2f}",
                        },
                        "mid_frequency": {
                            "signal_type": "trust_repair"
                            if path.mid_freq_impact < 0.5
                            else "trust_erosion",
                            "intensity": path.mid_freq_impact,
                            "description": f"Mid-frequency impact intensity {path.mid_freq_impact:.2f}",
                        },
                        "high_frequency": {
                            "signal_type": "meaning_protection"
                            if path.high_freq_impact > 0.5
                            else "meaning_drift",
                            "intensity": path.high_freq_impact,
                            "description": f"High-frequency impact intensity {path.high_freq_impact:.2f}",
                        },
                    },
                    "total_pain": path.total_pain,
                    "chain_visual": (
                        f"low({path.low_freq_impact:.2f}) -> "
                        f"mid({path.mid_freq_impact:.2f}) -> "
                        f"high({path.high_freq_impact:.2f})"
                    ),
                }
            )
        return paths

    def _build_soul_account(self, state: CivilizationState) -> Dict:
        debits = []
        credits = []

        if state.survival < 0.5:
            debits.append(
                {
                    "item": "survival_pressure",
                    "points": -12,
                    "basis": "Survival layer remains below stable range.",
                }
            )
        if state.coordination < 0.6:
            debits.append(
                {
                    "item": "coordination_friction",
                    "points": -8,
                    "basis": "Coordination layer shows unresolved friction.",
                }
            )
        if state.meaning >= 0.5:
            credits.append(
                {
                    "item": "meaning_retention",
                    "points": 4,
                    "basis": "Meaning layer remains above minimum continuity threshold.",
                }
            )

        initial_balance = 100
        debit_points = sum(item["points"] for item in debits)
        credit_points = sum(item["points"] for item in credits)
        balance = initial_balance + debit_points + credit_points

        if balance >= 90:
            health_status = "healthy"
        elif balance >= 75:
            health_status = "watch"
        else:
            health_status = "stressed"

        return {
            "initial_balance": initial_balance,
            "debits": debits,
            "credits": credits,
            "balance": balance,
            "health_status": health_status,
        }

    def _build_who_hurts(self, state: CivilizationState) -> Dict:
        return {
            "low_frequency": {
                "pain_bearer": "frontline operators",
                "description": "Delivery cost, ticket volume, or operational friction rises first.",
            },
            "mid_frequency": {
                "pain_bearer": "partners and reviewers",
                "description": "Trust repair work and coordination overhead expand next.",
            },
            "high_frequency": {
                "pain_bearer": "decision owners",
                "description": "Narrative pressure, accountability, and strategic ambiguity accumulate last.",
            },
        }

    def _build_frequency_plan(
        self,
        state: CivilizationState,
        system_state: str,
        spectrum_priority: str,
    ) -> Dict:
        interventions = []

        if state.coordination < 0.6:
            interventions.append(
                {
                    "name": "coordination_repair",
                    "target_frequency": "mid_frequency",
                    "description": "Add explicit review and cross-source consistency checks.",
                    "expected_effect": "Reduce trust erosion and repeated escalation.",
                    "urgency": "high",
                }
            )

        if state.survival < 0.5:
            interventions.append(
                {
                    "name": "survival_stabilization",
                    "target_frequency": "low_frequency",
                    "description": "Slow execution and reduce irreversible business action intensity.",
                    "expected_effect": "Lower operational blast radius.",
                    "urgency": "high",
                }
            )

        if not interventions:
            interventions.append(
                {
                    "name": "meaning_monitoring",
                    "target_frequency": "high_frequency",
                    "description": "Keep the system under observation and maintain explanation quality.",
                    "expected_effect": "Preserve interpretability and long-horizon continuity.",
                    "urgency": "medium",
                }
            )

        priority_text = {
            "survival": "stabilize survival first, then repair coordination, then revisit narrative meaning",
            "coordination": "repair coordination first, then reduce low-frequency pressure",
            "meaning": "preserve meaning continuity while monitoring lower layers",
        }.get(spectrum_priority, "maintain balanced observation across all three layers")

        return {
            "recommendation": priority_text,
            "priority": spectrum_priority,
            "system_state": system_state,
            "interventions": interventions,
        }

    def _build_narrative(
        self,
        state: CivilizationState,
        ess_result: ESSResult,
        system_state: str,
        runestone: Runestone,
    ) -> str:
        selected_pain = ess_result.paths[0].total_pain if ess_result.paths else 0.0
        return (
            f"The engine classified the system as {system_state}. "
            f"ESS selected path {ess_result.selected_option} with total pain {selected_pain:.3f}. "
            f"Final state = [survival={state.survival:.3f}, coordination={state.coordination:.3f}, "
            f"meaning={state.meaning:.3f}]. "
            f"Runestone {runestone.runestone_id} anchors the audit token for this evaluation."
        )
