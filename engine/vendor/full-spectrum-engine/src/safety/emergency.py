#!/usr/bin/env python3
"""
全频谱协议 · 紧急制动系统（BSRM）
系统最后的"熔断"防线
"""

from enum import Enum
from dataclasses import dataclass, field
from typing import Optional, List, Tuple
import time
import logging

from ..core.state import CivilizationState
from ..core.fshi import compute_fshi, fshi_risk_level


class BrakeState(Enum):
    NORMAL = "NORMAL"
    WARNING = "WARNING"
    TRIGGERED = "TRIGGERED"
    RECOVERY = "RECOVERY"


@dataclass
class EmergencyTriggerConfig:
    """紧急制动触发配置"""
    fshi_critical: float = 15.0       # FSHI < 15 触发
    survival_critical: float = 0.2    # S_l < 0.2 触发
    conflict_critical: float = 0.8    # 冲突密度 > 0.8 触发
    pain_critical: float = 0.9        # PainScore > 0.9 触发
    bomb_consecutive_limit: int = 3   # 连续觉性炸弹次数
    
    guardian_quorum: float = 2.0 / 3.0   # 守庙人2/3多签
    guardian_min_count: int = 5          # 最少守庙人参与
    
    require_human_reset: bool = True     # 必须人工复位
    reset_guardian_quorum: float = 2.0 / 3.0


@dataclass
class BrakeEvent:
    """紧急制动事件"""
    timestamp: float = field(default_factory=time.time)
    trigger_type: str = ""      # AUTO / MANUAL_GUARDIAN / MANUAL_ADMIN / EXTERNAL
    trigger_reason: str = ""
    trigger_value: float = 0.0
    threshold: float = 0.0
    triggered_by: str = ""
    resolved_at: Optional[float] = None
    resolved_by: Optional[str] = None


class EmergencyBrakeEngine:
    """紧急制动引擎 - BSRM实现"""
    
    def __init__(self, config: Optional[EmergencyTriggerConfig] = None):
        self.config = config or EmergencyTriggerConfig()
        self.state = BrakeState.NORMAL
        self.events: List[BrakeEvent] = []
        self._bomb_counter = 0
        self._is_braked = False
        self._shutdown_snapshot: Optional[Dict] = None
        self.logger = logging.getLogger("EmergencyBrake")
    
    def monitor(
        self,
        S: CivilizationState,
        fshi: float,
        conflict_density: float = 0.0,
        pain_score: float = 0.0,
        bomb_triggered: bool = False
    ) -> Tuple[BrakeState, Optional[BrakeEvent]]:
        """
        监控系统状态
        
        Returns:
            (state, event) 其中 event 在触发时返回
        """
        event = None
        
        # 1. FSHI 崩溃检查
        if fshi < self.config.fshi_critical:
            event = BrakeEvent(
                trigger_type="AUTO",
                trigger_reason="FSHI_CRITICAL",
                trigger_value=fshi,
                threshold=self.config.fshi_critical,
                triggered_by="system_monitor"
            )
        
        # 2. 生存层崩溃检查
        elif S.survival < self.config.survival_critical:
            event = BrakeEvent(
                trigger_type="AUTO",
                trigger_reason="SURVIVAL_CRITICAL",
                trigger_value=S.survival,
                threshold=self.config.survival_critical,
                triggered_by="system_monitor"
            )
        
        # 3. 冲突指数爆炸
        elif conflict_density > self.config.conflict_critical:
            event = BrakeEvent(
                trigger_type="AUTO",
                trigger_reason="CONFLICT_EXPLOSION",
                trigger_value=conflict_density,
                threshold=self.config.conflict_critical,
                triggered_by="system_monitor"
            )
        
        # 4. 痛苦指数超标
        elif pain_score > self.config.pain_critical:
            event = BrakeEvent(
                trigger_type="AUTO",
                trigger_reason="PAIN_SCORE_CRITICAL",
                trigger_value=pain_score,
                threshold=self.config.pain_critical,
                triggered_by="system_monitor"
            )
        
        # 5. 觉性炸弹连续触发
        if bomb_triggered:
            self._bomb_counter += 1
        else:
            self._bomb_counter = max(0, self._bomb_counter - 1)
        
        if self._bomb_counter >= self.config.bomb_consecutive_limit:
            event = BrakeEvent(
                trigger_type="AUTO",
                trigger_reason="BOMB_CONSECUTIVE",
                trigger_value=self._bomb_counter,
                threshold=self.config.bomb_consecutive_limit,
                triggered_by="system_monitor"
            )
        
        # 6. 状态转移
        if event:
            self._trigger_brake(event)
        else:
            self._update_state(S, fshi, conflict_density)
        
        return self.state, event
    
    def _trigger_brake(self, event: BrakeEvent):
        """执行紧急制动"""
        if self.state == BrakeState.TRIGGERED:
            return
        
        self.state = BrakeState.TRIGGERED
        self._is_braked = True
        self.events.append(event)
        
        self.logger.critical(
            f"EMERGENCY BRAKE TRIGGERED: {event.trigger_reason} "
            f"(value={event.trigger_value:.3f}, threshold={event.threshold:.3f})"
        )
        
        self._execute_shutdown()
    
    def _execute_shutdown(self):
        """执行系统关闭流程"""
        self._shutdown_snapshot = {
            "timestamp": time.time(),
            "state": self._current_state.__dict__ if hasattr(self, '_current_state') else None,
            "reason": self.events[-1].trigger_reason if self.events else "UNKNOWN",
            "trigger_type": self.events[-1].trigger_type if self.events else "UNKNOWN"
        }
    
    def _update_state(self, S: CivilizationState, fshi: float, conflict_density: float):
        """更新预警状态"""
        if self.state == BrakeState.TRIGGERED:
            return
        
        warnings = []
        if fshi < self.config.fshi_critical * 1.5:
            warnings.append(f"FSHI={fshi:.1f}")
        if S.survival < self.config.survival_critical * 1.5:
            warnings.append(f"SURVIVAL={S.survival:.3f}")
        if conflict_density > self.config.conflict_critical * 0.7:
            warnings.append(f"CONFLICT={conflict_density:.3f}")
        
        if warnings and self.state == BrakeState.NORMAL:
            self.state = BrakeState.WARNING
            self.logger.warning(f"EMERGENCY WARNING: {', '.join(warnings)}")
        elif not warnings and self.state == BrakeState.WARNING:
            self.state = BrakeState.NORMAL
            self.logger.info("Emergency warning cleared")
    
    def manual_trigger(
        self,
        triggered_by: str,
        reason: str,
        guardian_votes: Optional[List[str]] = None
    ) -> Tuple[bool, str]:
        """手动触发紧急制动"""
        if self.state == BrakeState.TRIGGERED:
            return False, "ALREADY_TRIGGERED"
        
        if guardian_votes and len(guardian_votes) < self.config.guardian_min_count:
            return False, f"INSUFFICIENT_GUARDIANS: {len(guardian_votes)} < {self.config.guardian_min_count}"
        
        event = BrakeEvent(
            trigger_type="MANUAL_GUARDIAN" if guardian_votes else "MANUAL_ADMIN",
            trigger_reason=reason,
            trigger_value=1.0,
            threshold=1.0,
            triggered_by=triggered_by
        )
        
        self._trigger_brake(event)
        return True, "TRIGGERED"
    
    def reset(self, reset_by: str, guardian_votes: Optional[List[str]] = None) -> Tuple[bool, str]:
        """复位紧急制动"""
        if self.state != BrakeState.TRIGGERED:
            return False, "NOT_TRIGGERED"
        
        if not self.config.require_human_reset:
            self._perform_reset(reset_by)
            return True, "RESET_AUTO"
        
        if not guardian_votes or len(guardian_votes) < self.config.guardian_min_count:
            return False, f"INSUFFICIENT_GUARDIANS: {len(guardian_votes)} < {self.config.guardian_min_count}"
        
        if len(guardian_votes) >= self.config.guardian_min_count:
            self._perform_reset(reset_by, guardian_votes)
            return True, "RESET_GUARDIAN"
        
        return False, "CONSENSUS_FAILED"
    
    def _perform_reset(self, reset_by: str, guardian_votes: Optional[List[str]] = None):
        """执行复位"""
        self.state = BrakeState.RECOVERY
        self._is_braked = False
        
        if self.events:
            self.events[-1].resolved_at = time.time()
            self.events[-1].resolved_by = reset_by
        
        self.logger.info(f"EMERGENCY BRAKE RESET by {reset_by}")
    
    def is_braked(self) -> bool:
        return self._is_braked
    
    def get_last_event(self) -> Optional[BrakeEvent]:
        return self.events[-1] if self.events else None
    
    def get_events(self) -> List[BrakeEvent]:
        return self.events.copy()
