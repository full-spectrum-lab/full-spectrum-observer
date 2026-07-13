#!/usr/bin/env python3
"""
全频谱协议 · 符石审计令牌
不可篡改的跨系统决策链记录
"""

import uuid
import time
import hashlib
import json
from dataclasses import dataclass, field
from typing import List, Optional, Dict, Any

from ..core.state import CivilizationState


@dataclass
class RiskVector:
    """
    八维语义风险向量。

    ⚠️ 字段语义说明（P0-2 修复）：
    - reversibility: 表示「不可逆风险强度」，值越高 = 决策后果越难撤销/越不可逆。
      该字段名保留以兼容协议规范（full-spectrum-ethics），但语义为 irreversibility。
      后续协议版本可能重命名为 rollback_difficulty。
    """
    survival_impact: float
    trust_impact: float
    meaning_impact: float
    reversibility: float  # 语义为 irreversibility：值越高 = 越不可逆
    explainability: float
    diffusivity: float
    urgency: float
    uncertainty: float
    
    def __post_init__(self):
        for k, v in self.__dict__.items():
            setattr(self, k, max(0.0, min(1.0, v)))
    
    def to_dict(self) -> Dict:
        return {
            "survival_impact": self.survival_impact,
            "trust_impact": self.trust_impact,
            "meaning_impact": self.meaning_impact,
            "reversibility": self.reversibility,
            "explainability": self.explainability,
            "diffusivity": self.diffusivity,
            "urgency": self.urgency,
            "uncertainty": self.uncertainty
        }


@dataclass
class ReasonField:
    """Reason 字段：ESS-{企业ID}-{规则版本号}"""
    enterprise_id: str
    rule_version: str
    
    def __str__(self) -> str:
        return f"ESS-{self.enterprise_id}-{self.rule_version}"
    
    @classmethod
    def parse(cls, reason_str: str) -> Optional["ReasonField"]:
        parts = reason_str.split("-")
        if len(parts) != 3 or parts[0] != "ESS":
            return None
        return cls(enterprise_id=parts[1], rule_version=parts[2])


@dataclass
class Runestone:
    """符石：跨企业审计令牌"""
    runestone_id: str
    timestamp: float
    decision: str
    reason: str
    risk_vector: RiskVector
    parent_runestone: Optional[str] = None
    agent_trail: List[str] = field(default_factory=list)
    ess_snapshot: Dict[str, Any] = field(default_factory=dict)
    causal_chain: Optional[Dict] = None
    signature: Optional[str] = None
    
    @classmethod
    def create(
        cls,
        decision: str,
        reason: str,
        risk_vector: RiskVector,
        parent: Optional[str] = None,
        agents: List[str] = None,
        ess_data: Dict = None,
        causal_chain: Dict = None,
        runestone_id: Optional[str] = None,
        timestamp: Optional[float] = None
    ) -> "Runestone":
        """创建新的符石"""
        return cls(
            runestone_id=runestone_id or f"RS_{uuid.uuid4().hex[:16]}",
            timestamp=time.time() if timestamp is None else timestamp,
            decision=decision,
            reason=reason,
            risk_vector=risk_vector,
            parent_runestone=parent,
            agent_trail=agents or [],
            ess_snapshot=ess_data or {},
            causal_chain=causal_chain
        )
    
    def to_dict(self) -> Dict:
        return {
            "runestone_id": self.runestone_id,
            "timestamp": self.timestamp,
            "decision": self.decision,
            "reason": self.reason,
            "risk_vector": self.risk_vector.to_dict(),
            "parent_runestone": self.parent_runestone,
            "agent_trail": self.agent_trail,
            "ess_snapshot": self.ess_snapshot,
            "causal_chain": self.causal_chain,
            "signature": self.signature
        }
    
    def compute_hash(self) -> str:
        """计算符石的哈希值（用于链式验证）"""
        data = self.to_dict()
        data.pop("signature", None)
        return hashlib.sha256(json.dumps(data, sort_keys=True).encode()).hexdigest()
    
    def verify_chain(self, parent: "Runestone") -> bool:
        """验证符石链的完整性"""
        if self.parent_runestone != parent.runestone_id:
            return False
        return self.compute_hash() != parent.compute_hash()
