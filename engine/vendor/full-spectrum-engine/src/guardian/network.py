#!/usr/bin/env python3
"""
全频谱协议 · 守庙人网络
分布式治理节点：人类+AI混合，2/3多签投票
"""

from dataclasses import dataclass, field
from typing import List, Optional, Dict, Any, Tuple
from enum import Enum
import time
import uuid


class GuardianType(Enum):
    HUMAN = "human"
    AI = "ai"
    INSTITUTIONAL = "institutional"


class GuardianStatus(Enum):
    ACTIVE = "active"
    INACTIVE = "inactive"
    SUSPENDED = "suspended"


@dataclass
class GuardianNode:
    """守庙人节点"""
    node_id: str
    node_type: GuardianType
    status: GuardianStatus = GuardianStatus.ACTIVE
    trust_score: float = 0.8
    region: str = "default"
    term_start: float = field(default_factory=time.time)
    term_end: Optional[float] = None
    vote_history: List[Dict] = field(default_factory=list)
    
    def is_active(self) -> bool:
        return self.status == GuardianStatus.ACTIVE
    
    def is_eligible(self) -> bool:
        """检查是否在任期内"""
        if self.term_end is None:
            return True
        return time.time() < self.term_end


@dataclass
class Vote:
    """投票记录"""
    vote_id: str
    proposal_id: str
    guardian_id: str
    choice: int  # 选项索引
    timestamp: float = field(default_factory=time.time)
    weight: float = 1.0


@dataclass
class Proposal:
    """提案"""
    proposal_id: str
    title: str
    description: str
    options: List[str]
    status: str = "open"  # open / voting / closed / resolved
    created_at: float = field(default_factory=time.time)
    votes: List[Vote] = field(default_factory=list)
    result: Optional[int] = None  # 获胜选项索引


class GuardianNetwork:
    """守庙人网络"""
    
    QUORUM_RATIO = 2.0 / 3.0  # 2/3多签
    MIN_QUORUM = 5  # 最少参与节点数
    MAX_TERM_YEARS = 2
    MAX_TERMS = 2
    
    def __init__(self, guardians: Optional[List[GuardianNode]] = None):
        self.guardians: Dict[str, GuardianNode] = {}
        self.proposals: Dict[str, Proposal] = {}
        self._node_counter = 0
        
        if guardians:
            for g in guardians:
                self.add_guardian(g)
    
    def add_guardian(self, guardian: GuardianNode) -> str:
        """添加守庙人节点"""
        if guardian.node_id in self.guardians:
            raise ValueError(f"节点 {guardian.node_id} 已存在")
        self.guardians[guardian.node_id] = guardian
        return guardian.node_id
    
    def create_guardian(
        self,
        node_type: GuardianType,
        region: str = "default",
        trust_score: float = 0.8
    ) -> str:
        """创建守庙人节点"""
        node_id = f"guardian_{self._node_counter:04d}"
        self._node_counter += 1
        
        guardian = GuardianNode(
            node_id=node_id,
            node_type=node_type,
            region=region,
            trust_score=trust_score
        )
        self.add_guardian(guardian)
        return node_id
    
    def get_guardian(self, node_id: str) -> Optional[GuardianNode]:
        return self.guardians.get(node_id)
    
    def get_active_guardians(self) -> List[GuardianNode]:
        """获取活跃守庙人"""
        return [g for g in self.guardians.values() if g.is_active() and g.is_eligible()]
    
    def get_guardians_by_type(self, node_type: GuardianType) -> List[GuardianNode]:
        return [g for g in self.guardians.values() if g.node_type == node_type]
    
    def raise_proposal(
        self,
        title: str,
        description: str,
        options: List[str]
    ) -> str:
        """发起提案"""
        proposal_id = f"prop_{uuid.uuid4().hex[:8]}"
        proposal = Proposal(
            proposal_id=proposal_id,
            title=title,
            description=description,
            options=options
        )
        self.proposals[proposal_id] = proposal
        return proposal_id
    
    def vote(self, proposal_id: str, guardian_id: str, choice: int) -> bool:
        """
        投票
        Returns: 是否成功
        """
        proposal = self.proposals.get(proposal_id)
        if not proposal:
            return False
        
        if proposal.status != "open":
            return False
        
        guardian = self.get_guardian(guardian_id)
        if not guardian or not guardian.is_active():
            return False
        
        # 检查是否已投票
        for v in proposal.votes:
            if v.guardian_id == guardian_id:
                return False
        
        vote = Vote(
            vote_id=f"vote_{uuid.uuid4().hex[:8]}",
            proposal_id=proposal_id,
            guardian_id=guardian_id,
            choice=choice,
            weight=guardian.trust_score
        )
        proposal.votes.append(vote)
        guardian.vote_history.append({
            "proposal_id": proposal_id,
            "choice": choice,
            "timestamp": vote.timestamp
        })
        return True
    
    def close_voting(self, proposal_id: str) -> bool:
        """关闭投票"""
        proposal = self.proposals.get(proposal_id)
        if not proposal:
            return False
        if proposal.status != "open":
            return False
        proposal.status = "voting"
        return True
    
    def resolve(self, proposal_id: str) -> Tuple[bool, Optional[int], str]:
        """
        裁决提案
        
        Returns:
            (是否成功, 获胜选项索引, 消息)
        """
        proposal = self.proposals.get(proposal_id)
        if not proposal:
            return False, None, "提案不存在"
        
        if proposal.status == "resolved":
            return False, None, "已裁决"
        
        active_count = len(self.get_active_guardians())
        quorum = max(self.MIN_QUORUM, int(active_count * self.QUORUM_RATIO))
        
        if len(proposal.votes) < quorum:
            return False, None, f"投票不足: {len(proposal.votes)}/{quorum}"
        
        # 统计票数（加权）
        vote_counts = {}
        total_weight = 0
        for vote in proposal.votes:
            vote_counts[vote.choice] = vote_counts.get(vote.choice, 0) + vote.weight
            total_weight += vote.weight
        
        # 找出获胜选项
        winner = max(vote_counts, key=vote_counts.get)
        winner_weight = vote_counts[winner]
        
        # 检查是否达到2/3
        if winner_weight / total_weight < self.QUORUM_RATIO:
            return False, None, f"未达到2/3多签: {winner_weight/total_weight:.2%}"
        
        proposal.result = winner
        proposal.status = "resolved"
        
        return True, winner, f"选项 {winner} 获胜"
    
    def get_proposal(self, proposal_id: str) -> Optional[Proposal]:
        return self.proposals.get(proposal_id)
    
    def get_open_proposals(self) -> List[Proposal]:
        return [p for p in self.proposals.values() if p.status in ["open", "voting"]]
    
    def get_resolved_proposals(self) -> List[Proposal]:
        return [p for p in self.proposals.values() if p.status == "resolved"]
