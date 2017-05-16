﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.BouncyCastle.Math;
using Stratis.Bitcoin.Consensus;
using Stratis.Bitcoin.Utilities;

namespace Stratis.Bitcoin.Miner
{
	public class ReserveScript
	{
		public Script reserveSfullNodecript { get; set; }
	}

	public class Mining
    {
		// Default for -blockmintxfee, which sets the minimum feerate for a transaction in blocks created by mining code 
		public const int DefaultBlockMinTxFee = 1000;
		// Default for -blockmaxsize, which controls the maximum size of block the mining code will create 
		public const int DefaultBlockMaxSize = 750000;
		// Default for -blockmaxweight, which controls the range of block weights the mining code will create 
		public const int DefaultBlockMaxWeight = 3000000;

		private readonly FullNode fullNode;
	    private readonly ConsensusLoop consensusLoop;
	    private readonly ConcurrentChain chain;
	    private readonly Network network;
	    private readonly IDateTimeProvider dateTimeProvider;
	    private readonly BlockAssemblerFactory blockAssemblerFactory;

	    public Mining(FullNode fullNode, ConsensusLoop consensusLoop, ConcurrentChain chain, Network network, IDateTimeProvider dateTimeProvider, BlockAssemblerFactory blockAssemblerFactory)
	    {
		    this.fullNode = fullNode;
		    this.consensusLoop = consensusLoop;
		    this.chain = chain;
		    this.network = network;
		    this.dateTimeProvider = dateTimeProvider;
		    this.blockAssemblerFactory = blockAssemblerFactory;
	    }

		const int nInnerLoopCount = 0x10000;

		public List<uint256> GenerateBlocks(ReserveScript reserveScript, int generate, int maxTries, bool keepScript)
	    {
			int nHeightStart = 0;
			int nHeightEnd = 0;
			int nHeight = 0;

			nHeightStart = this.chain.Height;
			nHeight = nHeightStart;
			nHeightEnd = nHeightStart + generate;
			int nExtraNonce = 0;
			var blocks = new List<uint256>();

		    if (this.chain.Tip != this.consensusLoop.Tip)
			    return blocks;

			while (nHeight < nHeightEnd)
			{
				var pblocktemplate = this.blockAssemblerFactory.Create().CreateNewBlock(reserveScript.reserveSfullNodecript);
				BlockAssembler.IncrementExtraNonce(pblocktemplate.Block, this.chain.Tip, nExtraNonce);
				var pblock = pblocktemplate.Block;

				while (maxTries > 0 && pblock.Header.Nonce < nInnerLoopCount && !pblock.CheckProofOfWork())
				{
					++pblock.Header.Nonce;
					--maxTries;
				}

				if (maxTries == 0)
					break;

				if (pblock.Header.Nonce == nInnerLoopCount)
					continue;

				var newChain = new ChainedBlock(pblock.Header, pblock.GetHash(), fullNode.Chain.Tip);
				this.chain.SetTip(newChain);

				var blockResult = new BlockResult {Block = pblock };
				fullNode.ConsensusLoop.AcceptBlock(new ContextInformation(blockResult, fullNode.Network.Consensus));

				if (blockResult.ChainedBlock == null)
					break; //reorg

				if (blockResult.Error != null)
					return blocks;
				
				// similar logic to what's in the full node code
				fullNode.ChainBehaviorState.HighestValidatedPoW = fullNode.ConsensusLoop.Tip;
				fullNode.Signals.Blocks.Broadcast(pblock);

				++nHeight;
				blocks.Add(pblock.GetHash());

				// ensure the block is written to disk
				var retry = 0;
				while (++retry < maxTries &&
				       !this.fullNode.BlockStoreManager.BlockRepository.ExistAsync(blockResult.ChainedBlock.HashBlock).GetAwaiter().GetResult())
					Thread.Sleep(100);
			}

		    return blocks;
	    }

		public static Target GetWorkRequired(NBitcoin.Consensus consensus, ChainedBlock chainedBlock)
		{
			// Genesis block
			if (chainedBlock.Height == 0)
				return consensus.PowLimit;
			var nProofOfWorkLimit = consensus.PowLimit;
			var pindexLast = chainedBlock.Previous;
			var height = chainedBlock.Height;

			if (pindexLast == null)
				return nProofOfWorkLimit;

			// Only change once per interval
			if ((height) % consensus.DifficultyAdjustmentInterval != 0)
			{
				if (consensus.PowAllowMinDifficultyBlocks)
				{
					// Special difficulty rule for testnet:
					// If the new block's timestamp is more than 2* 10 minutes
					// then allow mining of a min-difficulty block.
					if (chainedBlock.Header.BlockTime > pindexLast.Header.BlockTime + TimeSpan.FromTicks(consensus.PowTargetSpacing.Ticks * 2))
						return nProofOfWorkLimit;
					else
					{
						// Return the last non-special-min-difficulty-rules-block
						ChainedBlock pindex = pindexLast;
						while (pindex.Previous != null && (pindex.Height % consensus.DifficultyAdjustmentInterval) != 0 && pindex.Header.Bits == nProofOfWorkLimit)
							pindex = pindex.Previous;
						return pindex.Header.Bits;
					}
				}
				return pindexLast.Header.Bits;
			}

			// Go back by what we want to be 14 days worth of blocks
			var pastHeight = pindexLast.Height - (consensus.DifficultyAdjustmentInterval - 1);
			ChainedBlock pindexFirst = chainedBlock.EnumerateToGenesis().FirstOrDefault(o => o.Height == pastHeight);
			Guard.Assert(pindexFirst != null);

			if (consensus.PowNoRetargeting)
				return pindexLast.Header.Bits;

			// Limit adjustment step
			var nActualTimespan = pindexLast.Header.BlockTime - pindexFirst.Header.BlockTime;
			if (nActualTimespan < TimeSpan.FromTicks(consensus.PowTargetTimespan.Ticks / 4))
				nActualTimespan = TimeSpan.FromTicks(consensus.PowTargetTimespan.Ticks / 4);
			if (nActualTimespan > TimeSpan.FromTicks(consensus.PowTargetTimespan.Ticks * 4))
				nActualTimespan = TimeSpan.FromTicks(consensus.PowTargetTimespan.Ticks * 4);

			// Retarget
			var bnNew = pindexLast.Header.Bits.ToBigInteger();
			bnNew = bnNew.Multiply(BigInteger.ValueOf((long)nActualTimespan.TotalSeconds));
			bnNew = bnNew.Divide(BigInteger.ValueOf((long)consensus.PowTargetTimespan.TotalSeconds));
			var newTarget = new Target(bnNew);
			if (newTarget > nProofOfWorkLimit)
				newTarget = nProofOfWorkLimit;

			return newTarget;
		}
	}
}
