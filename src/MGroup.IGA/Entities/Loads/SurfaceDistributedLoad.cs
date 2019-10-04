using System;
using System.Collections.Generic;
using System.Text;
using MGroup.IGA.Interfaces;
using MGroup.MSolve.Discretization.FreedomDegrees;

namespace MGroup.IGA.Entities.Loads
{
	public class SurfaceDistributedLoad : ISurfaceLoad
	{
		public SurfaceDistributedLoad(double magnitude, IDofType loadedDof)
		{
			Magnitude = magnitude;
			Dof = loadedDof;
		}

		public double Magnitude { get; private set; }

		public IDofType Dof { get; private set; }
	}
}
