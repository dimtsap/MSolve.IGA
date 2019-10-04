using System;
using System.Collections.Generic;
using System.Text;
using MGroup.IGA.Interfaces;

namespace MGroup.IGA.Entities.Loads
{
	public class SurfacePressureLoad : ISurfaceLoad
	{
		public SurfacePressureLoad(double pressure) => Pressure = pressure;

		public double Pressure { get; private set; }
	}
}
