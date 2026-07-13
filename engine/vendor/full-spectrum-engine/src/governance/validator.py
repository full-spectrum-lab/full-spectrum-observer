#!/usr/bin/env python3
"""
全频谱协议 · 梦蝶四层校验器
L1 边界自知 · L3 成住坏空 · L4 能所纯度 · L5 觉性炸弹
"""

from dataclasses import dataclass
from typing import Tuple, Optional, Dict, Any
import time
import numpy as np

from ..core.state import CivilizationState, project_to_feasible


@dataclass
class ProtocolMetadata:
    """协议元数据"""
    creation_time: float
    expected_lifetime: float
    context_tags: list
    feedback_positive: int = 0
    feedback_negative: int = 0


@dataclass
class NengsuoMetrics:
    """能所纯度指标"""
    first_person_frequency: float = 0.0
    role_reference_density: float = 0.0
    expectation_adherence: float = 0.0
    abstraction_level: float = 0.0
    category_rigidity: float = 0.0
    framework_lock: float = 0.0


@dataclass
class LayerResult:
    name: str
    score: float
    passed: bool
    threshold: float
    correction_applied: bool = False


def compute_boundary_clarity(predictions: list, strategies: list) -> float:
    """
    L1 边界自知：计算边界清晰度
    """
    if not predictions or not strategies:
        return 0.0
    
    pred_std = np.std(predictions, axis=0) if hasattr(predictions[0], '__len__') else [0.5]
    uncertainty = min(1.0, np.mean(pred_std) / 0.5)
    
    strategy_std = np.std(strategies, axis=0)
    contradiction = min(1.0, np.mean(strategy_std) / 0.3)
    
    clarity = 1.0 - (uncertainty * 0.5 + contradiction * 0.5)
    return max(0.0, min(1.0, clarity))


def compute_nengsuo_purity(metrics: NengsuoMetrics) -> float:
    """
    L4 能所纯度：计算自由意志不被僵化框架吞噬的程度
    """
    identity_weight = (
        0.3 * metrics.first_person_frequency +
        0.3 * metrics.role_reference_density +
        0.4 * metrics.expectation_adherence
    )
    conceptual_weight = (
        0.3 * metrics.abstraction_level +
        0.3 * metrics.category_rigidity +
        0.4 * metrics.framework_lock
    )
    purity = 1.0 - (identity_weight * 0.5 + conceptual_weight * 0.5)
    return max(0.0, min(1.0, purity))


def compute_rigidity_score(meta: ProtocolMetadata, context: Dict) -> float:
    """
    L3 成住坏空：计算协议刚性指数
    """
    age_ratio = (time.time() - meta.creation_time) / meta.expected_lifetime
    age_score = min(1.0, age_ratio)
    
    context_score = 0.5  # 简化实现
    
    total = meta.feedback_positive + meta.feedback_negative
    feedback_score = meta.feedback_negative / max(1, total) if total > 0 else 0.5
    
    return 0.4 * age_score + 0.3 * context_score + 0.3 * feedback_score


class DreamButterflyValidator:
    """梦蝶协议栈完整校验器"""
    
    def __init__(self):
        self.bomb_counter = 0
        self.bomb_limit = 3
    
    def validate(
        self,
        S: CivilizationState,
        predictions: list,
        strategies: list,
        protocol_meta: Optional[ProtocolMetadata] = None,
        nengsuo_metrics: Optional[NengsuoMetrics] = None,
        context: Optional[Dict] = None
    ) -> Tuple[bool, str, Optional[CivilizationState]]:
        """
        完整四层约束校验
        
        Returns:
            (通过/拒绝, 原因, 修正后的状态)
        """
        # L1: 边界自知
        clarity = compute_boundary_clarity(predictions, strategies)
        if clarity < 0.3:
            return False, f"L1_FAILED: clarity={clarity:.3f}", None
        
        # L3: 成住坏空
        if protocol_meta:
            rigidity = compute_rigidity_score(protocol_meta, context or {})
            if rigidity >= 0.8:
                return False, f"L3_FAILED: rigidity={rigidity:.3f}", None
        
        # L4: 能所纯度
        if nengsuo_metrics:
            purity = compute_nengsuo_purity(nengsuo_metrics)
            if purity < 0.7:
                # 尝试修正
                S_corrected = self._correct_nengsuo(S)
                purity_corrected = compute_nengsuo_purity(self._get_corrected_metrics(nengsuo_metrics))
                if purity_corrected < 0.7:
                    return False, f"L4_FAILED: purity={purity:.3f}", None
                S = S_corrected
        
        # L5: 觉性炸弹
        if not S.is_feasible():
            self.bomb_counter += 1
            if self.bomb_counter >= self.bomb_limit:
                S_bombed = project_to_feasible(S)
                self.bomb_counter = 0
                return True, "L5_BOMB_DETONATED", S_bombed
        else:
            self.bomb_counter = 0
        
        return True, "ALL_PASSED", S
    
    def _correct_nengsuo(self, S: CivilizationState) -> CivilizationState:
        """能所纯度修正"""
        return CivilizationState(
            survival=S.survival,
            coordination=min(0.7, S.coordination),
            meaning=max(0.4, S.meaning)
        )
    
    def _get_corrected_metrics(self, metrics: NengsuoMetrics) -> NengsuoMetrics:
        """修正后的能所指标"""
        return NengsuoMetrics(
            first_person_frequency=metrics.first_person_frequency * 0.7,
            role_reference_density=metrics.role_reference_density * 0.7,
            expectation_adherence=metrics.expectation_adherence * 0.8,
            abstraction_level=metrics.abstraction_level * 0.8,
            category_rigidity=metrics.category_rigidity * 0.7,
            framework_lock=metrics.framework_lock * 0.7
        )
