﻿namespace RavenFS.Synchronization.Rdc
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Threading.Tasks;
	using RavenFS.Synchronization.Rdc.Wrapper;

	public class NeedListParser
	{
		public static async Task ParseAsync(IPartialDataAccess source, IPartialDataAccess seed, Stream output, IEnumerable<RdcNeed> needList)
		{
			foreach (var item in needList)
			{
				switch (item.BlockType)
				{
					case RdcNeedType.Source:
						await source.CopyToAsync(output, Convert.ToInt64(item.FileOffset), Convert.ToInt64(item.BlockLength));
						break;
					case RdcNeedType.Seed:
						await seed.CopyToAsync(output, Convert.ToInt64(item.FileOffset), Convert.ToInt64(item.BlockLength));
						break;
					default:
						throw new NotSupportedException();
				}
			}
		}
	}
}
