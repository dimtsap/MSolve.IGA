using System.Diagnostics.Contracts;
using MGroup.MSolve.Discretization.Commons;

namespace MGroup.IGA.Elements
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	using MGroup.IGA.Entities;
	using MGroup.IGA.Entities.Loads;
	using MGroup.IGA.Interfaces;
	using MGroup.IGA.SupportiveClasses;
	using MGroup.LinearAlgebra.Matrices;
	using MGroup.LinearAlgebra.Vectors;
	using MGroup.Materials.Interfaces;
	using MGroup.MSolve.Discretization;
	using MGroup.MSolve.Discretization.FreedomDegrees;
	using MGroup.MSolve.Discretization.Interfaces;
	using MGroup.MSolve.Discretization.Loads;
	using MGroup.MSolve.Discretization.Mesh;

	public class NurbsKirchhoffLoveShellElementNL : Element, IStructuralIsogeometricElement, ISurfaceLoadedElement
	{
		protected static readonly IDofType[] ControlPointDofTypes = { StructuralDof.TranslationX, StructuralDof.TranslationY, StructuralDof.TranslationZ };
		private IDofType[][] dofTypes;

		private Dictionary<GaussLegendrePoint3D, List<GaussLegendrePoint3D>> thicknessIntegrationPoints =
			new Dictionary<GaussLegendrePoint3D, List<GaussLegendrePoint3D>>();

		private Dictionary<GaussLegendrePoint3D, Dictionary<GaussLegendrePoint3D, IShellMaterial>>
			materialsAtThicknessGP = new Dictionary<GaussLegendrePoint3D, Dictionary<GaussLegendrePoint3D, IShellMaterial>>();

		private bool isInitialized;
		private double[] _solution;

		public NurbsKirchhoffLoveShellElementNL(IShellMaterial shellMaterial, IList<Knot> elementKnots, IList<ControlPoint> elementControlPoints, Patch patch, double thickness)
		{
			Contract.Requires(shellMaterial != null);
			this.Patch = patch;
			this.Thickness = thickness;
			foreach (var knot in elementKnots)
			{
				if (!KnotsDictionary.ContainsKey(knot.ID))
					this.KnotsDictionary.Add(knot.ID, knot);
			}

			_solution = new double[3 * elementControlPoints.Count];

			foreach (var controlPoint in elementControlPoints)
			{
				if (!ControlPointsDictionary.ContainsKey(controlPoint.ID))
					ControlPointsDictionary.Add(controlPoint.ID, controlPoint);
			}
			CreateElementGaussPoints(this);
			foreach (var medianSurfaceGP in thicknessIntegrationPoints.Keys)
			{
				materialsAtThicknessGP.Add(medianSurfaceGP, new Dictionary<GaussLegendrePoint3D, IShellMaterial>());
				foreach (var point in thicknessIntegrationPoints[medianSurfaceGP])
				{
					materialsAtThicknessGP[medianSurfaceGP].Add(point, shellMaterial.Clone());
				}
			}
		}

		public CellType CellType { get; } = CellType.Unknown;

		public IElementDofEnumerator DofEnumerator { get; set; } = new GenericDofEnumerator();

		public ElementDimensions ElementDimensions => ElementDimensions.ThreeD;

		public bool MaterialModified => false;

		public double[] CalculateAccelerationForces(IElement element, IList<MassAccelerationLoad> loads) => throw new NotImplementedException();

		public double[,] CalculateDisplacementsForPostProcessing(Element element, Matrix localDisplacements)
		{
			var nurbsElement = (NurbsKirchhoffLoveShellElementNL)element;
			var knotParametricCoordinatesKsi = Vector.CreateFromArray(Knots.Select(k => k.Ksi).ToArray());
			var knotParametricCoordinatesHeta = Vector.CreateFromArray(Knots.Select(k => k.Heta).ToArray());

			var nurbs = new Nurbs2D(nurbsElement, nurbsElement.ControlPoints.ToArray(), knotParametricCoordinatesKsi, knotParametricCoordinatesHeta);

			var knotDisplacements = new double[4, 3];
			var paraviewKnotRenumbering = new int[] { 0, 3, 1, 2 };
			for (var j = 0; j < knotDisplacements.GetLength(0); j++)
			{
				for (int i = 0; i < element.ControlPoints.Count(); i++)
				{
					knotDisplacements[paraviewKnotRenumbering[j], 0] +=
						nurbs.NurbsValues[i, j] * localDisplacements[i, 0];
					knotDisplacements[paraviewKnotRenumbering[j], 1] +=
						nurbs.NurbsValues[i, j] * localDisplacements[i, 1];
					knotDisplacements[paraviewKnotRenumbering[j], 2] +=
						nurbs.NurbsValues[i, j] * localDisplacements[i, 2];
				}
			}

			return knotDisplacements;
		}

		public double[] CalculateForces(IElement element, double[] localDisplacements, double[] localdDisplacements)
		{
			var shellElement = (NurbsKirchhoffLoveShellElementNL)element;
			var controlPoints = shellElement.ControlPoints.ToArray();
			var elementNodalForces = new double[shellElement.ControlPointsDictionary.Count * 3];

			var elementMembraneForces = new double[shellElement.ControlPointsDictionary.Count * 3];
			var elementBendingForces = new double[shellElement.ControlPointsDictionary.Count * 3];

			_solution = localDisplacements;

			var newControlPoints = CurrentControlPoint(controlPoints);
			//var newControlPoints = controlPoints;

			var nurbs = new Nurbs2D(shellElement, shellElement.ControlPoints.ToArray());
			var gaussPoints = materialsAtThicknessGP.Keys.ToArray();

			for (int j = 0; j < gaussPoints.Length; j++)
			{
				var jacobianMatrix = CalculateJacobian(newControlPoints, nurbs, j);

				var hessianMatrix = CalculateHessian(newControlPoints, nurbs, j);

				var surfaceBasisVector1 = CalculateSurfaceBasisVector1(jacobianMatrix, 0);

				var surfaceBasisVector2 = CalculateSurfaceBasisVector1(jacobianMatrix, 1);

				var surfaceBasisVector3 = new[]
				{
					surfaceBasisVector1[1] * surfaceBasisVector2[2] - surfaceBasisVector1[2] * surfaceBasisVector2[1],
					surfaceBasisVector1[2] * surfaceBasisVector2[0] - surfaceBasisVector1[0] * surfaceBasisVector2[2],
					surfaceBasisVector1[0] * surfaceBasisVector2[1] - surfaceBasisVector1[1] * surfaceBasisVector2[0],
				};

				double norm = surfaceBasisVector3.Sum(t => t * t);

				var J1 = Math.Sqrt(norm);

				for (int i = 0; i < surfaceBasisVector3.Length; i++)
					surfaceBasisVector3[i] = surfaceBasisVector3[i] / J1;

				var surfaceBasisVectorDerivative1 = CalculateSurfaceBasisVector1(hessianMatrix, 0);
				var surfaceBasisVectorDerivative2 = CalculateSurfaceBasisVector1(hessianMatrix, 1);
				var surfaceBasisVectorDerivative12 = CalculateSurfaceBasisVector1(hessianMatrix, 2);

				var Bmembrane = CalculateMembraneDeformationMatrix(newControlPoints, nurbs, j, surfaceBasisVector1,
					surfaceBasisVector2);
				var Bbending = CalculateBendingDeformationMatrix(newControlPoints, surfaceBasisVector3, nurbs, j, surfaceBasisVector2,
					surfaceBasisVectorDerivative1, surfaceBasisVector1, J1, surfaceBasisVectorDerivative2,
					surfaceBasisVectorDerivative12);

				var (membraneForces, bendingMoments) =
					IntegratedStressesOverThickness(gaussPoints[j]);

				var wfactor = InitialJ1[j] * gaussPoints[j].WeightFactor;

				

				for (int i = 0; i < Bmembrane.GetLength(1); i++)
				{
					for (int k = 0; k < Bmembrane.GetLength(0); k++)
					{
						elementNodalForces[i] += (Bmembrane[k, i] * membraneForces[k] * wfactor +
												 Bbending[k, i] * bendingMoments[k] * wfactor);

						elementMembraneForces[i] += Bmembrane[k, i] * membraneForces[k] * wfactor;
						elementBendingForces[i] += Bbending[k, i] * bendingMoments[k] * wfactor;
					}
				}
			}

			return elementNodalForces;
		}

		public double[] CalculateForcesForLogging(IElement element, double[] localDisplacements)
		{
			throw new NotImplementedException();
		}

		public Dictionary<int, double> CalculateLoadingCondition(Element element, Edge edge, NeumannBoundaryCondition neumann) => throw new NotImplementedException();

		public Dictionary<int, double> CalculateLoadingCondition(Element element, Face face, NeumannBoundaryCondition neumann) => throw new NotImplementedException();

		public Dictionary<int, double> CalculateLoadingCondition(Element element, Edge edge, PressureBoundaryCondition pressure) => throw new NotImplementedException();

		public Dictionary<int, double> CalculateLoadingCondition(Element element, Face face, PressureBoundaryCondition pressure) => throw new NotImplementedException();

		public Tuple<double[], double[]> CalculateStresses(IElement element, double[] localDisplacements, double[] localdDisplacements)
		{
			var shellElement = (NurbsKirchhoffLoveShellElementNL)element;
			var elementControlPoints = shellElement.ControlPoints.ToArray();
			var nurbs = new Nurbs2D(shellElement, elementControlPoints);

			_solution = localDisplacements;

			var newControlPoints = CurrentControlPoint(elementControlPoints);

			
			//var newControlPoints = elementControlPoints;

			var midsurfaceGP = materialsAtThicknessGP.Keys.ToArray();
			for (var j = 0; j < midsurfaceGP.Length; j++)
			{
				var jacobianMatrix = CalculateJacobian(newControlPoints, nurbs, j);

				var hessianMatrix = CalculateHessian(newControlPoints, nurbs, j);

				var surfaceBasisVector1 = CalculateSurfaceBasisVector1(jacobianMatrix, 0);

				var surfaceBasisVector2 = CalculateSurfaceBasisVector1(jacobianMatrix, 1);

				var surfaceBasisVector3 = new[]
				{
					surfaceBasisVector1[1] * surfaceBasisVector2[2] - surfaceBasisVector1[2] * surfaceBasisVector2[1],
					surfaceBasisVector1[2] * surfaceBasisVector2[0] - surfaceBasisVector1[0] * surfaceBasisVector2[2],
					surfaceBasisVector1[0] * surfaceBasisVector2[1] - surfaceBasisVector1[1] * surfaceBasisVector2[0]
				};

				var norm = surfaceBasisVector3.Sum(t => t * t);
				var J1 = Math.Sqrt(norm);

				var unitVector3 = new double[]
				{
					surfaceBasisVector3[0] / J1, surfaceBasisVector3[1] / J1, surfaceBasisVector3[2] / J1
				};

				var surfaceBasisVectorDerivative1 = CalculateSurfaceBasisVector1(hessianMatrix, 0);
				var surfaceBasisVectorDerivative2 = CalculateSurfaceBasisVector1(hessianMatrix, 1);
				var surfaceBasisVectorDerivative12 = CalculateSurfaceBasisVector1(hessianMatrix, 2);

				var A11 = initialSurfaceBasisVectors1[j][0] * initialSurfaceBasisVectors1[j][0] +
						  initialSurfaceBasisVectors1[j][1] * initialSurfaceBasisVectors1[j][1] +
						  initialSurfaceBasisVectors1[j][2] * initialSurfaceBasisVectors1[j][2];

				var A22 = initialSurfaceBasisVectors2[j][0] * initialSurfaceBasisVectors2[j][0] +
						 initialSurfaceBasisVectors2[j][1] * initialSurfaceBasisVectors2[j][1] +
						 initialSurfaceBasisVectors2[j][2] * initialSurfaceBasisVectors2[j][2];

				var A12 = initialSurfaceBasisVectors1[j][0] * initialSurfaceBasisVectors2[j][0] +
						  initialSurfaceBasisVectors1[j][1] * initialSurfaceBasisVectors2[j][1] +
						  initialSurfaceBasisVectors1[j][2] * initialSurfaceBasisVectors2[j][2];

				var a11 = surfaceBasisVector1[0] * surfaceBasisVector1[0] +
						  surfaceBasisVector1[1] * surfaceBasisVector1[1] +
						  surfaceBasisVector1[2] * surfaceBasisVector1[2];

				var a22 = surfaceBasisVector2[0] * surfaceBasisVector2[0] +
						  surfaceBasisVector2[1] * surfaceBasisVector2[1] +
						  surfaceBasisVector2[2] * surfaceBasisVector2[2];

				var a12 = surfaceBasisVector1[0] * surfaceBasisVector2[0] +
						  surfaceBasisVector1[1] * surfaceBasisVector2[1] +
						  surfaceBasisVector1[2] * surfaceBasisVector2[2];

				var membraneStrain = new double[] { 0.5 * (a11 - A11), 0.5 * (a22 - A22), a12 - A12 };

				var B11 = initialSurfaceBasisVectorDerivative1[j][0] * initialUnitSurfaceBasisVectors3[j][0] +
						  initialSurfaceBasisVectorDerivative1[j][1] * initialUnitSurfaceBasisVectors3[j][1] +
						  initialSurfaceBasisVectorDerivative1[j][2] * initialUnitSurfaceBasisVectors3[j][2];

				var B22 = initialSurfaceBasisVectorDerivative2[j][0] * initialUnitSurfaceBasisVectors3[j][0] +
						  initialSurfaceBasisVectorDerivative2[j][1] * initialUnitSurfaceBasisVectors3[j][1] +
						  initialSurfaceBasisVectorDerivative2[j][2] * initialUnitSurfaceBasisVectors3[j][2];

				var B12 = initialSurfaceBasisVectorDerivative12[j][0] * initialUnitSurfaceBasisVectors3[j][0] +
						  initialSurfaceBasisVectorDerivative12[j][1] * initialUnitSurfaceBasisVectors3[j][1] +
						  initialSurfaceBasisVectorDerivative12[j][2] * initialUnitSurfaceBasisVectors3[j][2];

				var b11 = surfaceBasisVectorDerivative1[0] * unitVector3[0] +
						  surfaceBasisVectorDerivative1[1] * unitVector3[1] +
						  surfaceBasisVectorDerivative1[2] * unitVector3[2];

				var b22 = surfaceBasisVectorDerivative2[0] * unitVector3[0] +
						  surfaceBasisVectorDerivative2[1] * unitVector3[1] +
						  surfaceBasisVectorDerivative2[2] * unitVector3[2];

				var b12 = surfaceBasisVectorDerivative12[0] * unitVector3[0] +
						 surfaceBasisVectorDerivative12[1] * unitVector3[1] +
						 surfaceBasisVectorDerivative12[2] * unitVector3[2];

				var bendingStrain = new double[] { b11 - B11, b22 - B22, 2*b12 - 2*B12 };

				//var bendingStrain = new double[] { -(b11 - B11), -(b22 - B22), -(2 * b12 - 2 * B12) };

				foreach (var keyValuePair in materialsAtThicknessGP[midsurfaceGP[j]])
				{
					var thicknessPoint = keyValuePair.Key;
					var material = keyValuePair.Value;
					var gpStrain = new double[bendingStrain.Length];
					var z = thicknessPoint.Zeta;
					for (var i = 0; i < bendingStrain.Length; i++)
					{
						gpStrain[i] += membraneStrain[i] + bendingStrain[i] * z;
					}

					material.UpdateMaterial(gpStrain);
				}
			}

			return new Tuple<double[], double[]>(new double[0], new double[0]);
		}

		public Dictionary<int, double> CalculateSurfaceDistributedLoad(Element element, IDofType loadedDof, double loadMagnitude)
		{
			var shellElement = (NurbsKirchhoffLoveShellElementNL)element;
			var elementControlPoints = shellElement.ControlPoints.ToArray();
			var gaussPoints = CreateElementGaussPoints(shellElement);
			var distributedLoad = new Dictionary<int, double>();
			var nurbs = new Nurbs2D(shellElement, elementControlPoints);

			for (var j = 0; j < gaussPoints.Count; j++)
			{
				var jacobianMatrix = CalculateJacobian(elementControlPoints, nurbs, j);
				var surfaceBasisVector1 = CalculateSurfaceBasisVector1(jacobianMatrix, 0);
				var surfaceBasisVector2 = CalculateSurfaceBasisVector1(jacobianMatrix, 1);
				var surfaceBasisVector3 = surfaceBasisVector1.CrossProduct(surfaceBasisVector2);
				var J1 = surfaceBasisVector3.Norm2();
				surfaceBasisVector3.ScaleIntoThis(1 / J1);

				for (int i = 0; i < elementControlPoints.Length; i++)
				{
					var loadedDofIndex = ControlPointDofTypes.FindFirstIndex(loadedDof);
					if (!element.Model.GlobalDofOrdering.GlobalFreeDofs.Contains(elementControlPoints[i], loadedDof))
						continue;
					var dofId = element.Model.GlobalDofOrdering.GlobalFreeDofs[elementControlPoints[i], loadedDof];

					if (distributedLoad.ContainsKey(dofId))
					{
						distributedLoad[dofId] += loadMagnitude * J1 *
												  nurbs.NurbsValues[i, j] * gaussPoints[j].WeightFactor;
					}
					else
					{
						distributedLoad.Add(dofId, loadMagnitude * nurbs.NurbsValues[i, j] * J1 * gaussPoints[j].WeightFactor);
					}
				}
			}

			return distributedLoad;
		}

		public Dictionary<int, double> CalculateSurfacePressure(Element element, double pressureMagnitude)
		{
			var shellElement = (NurbsKirchhoffLoveShellElementNL)element;
			var elementControlPoints = shellElement.ControlPoints.ToArray();
			var gaussPoints = CreateElementGaussPoints(shellElement);
			var pressureLoad = new Dictionary<int, double>();
			var nurbs = new Nurbs2D(shellElement, elementControlPoints);

			for (var j = 0; j < gaussPoints.Count; j++)
			{
				var jacobianMatrix = CalculateJacobian(elementControlPoints, nurbs, j);
				var surfaceBasisVector1 = CalculateSurfaceBasisVector1(jacobianMatrix, 0);
				var surfaceBasisVector2 = CalculateSurfaceBasisVector1(jacobianMatrix, 1);
				var surfaceBasisVector3 = surfaceBasisVector1.CrossProduct(surfaceBasisVector2);
				var J1 = surfaceBasisVector3.Norm2();
				surfaceBasisVector3.ScaleIntoThis(1 / J1);

				for (int i = 0; i < elementControlPoints.Length; i++)
				{
					for (int k = 0; k < ControlPointDofTypes.Length; k++)
					{
						int dofId = element.Model.GlobalDofOrdering.GlobalFreeDofs[elementControlPoints[i], ControlPointDofTypes[k]];

						if (pressureLoad.ContainsKey(dofId))
						{
							pressureLoad[dofId] += pressureMagnitude * surfaceBasisVector3[k] *
												   nurbs.NurbsValues[i, j] * gaussPoints[j].WeightFactor;
						}
						else
						{
							pressureLoad.Add(dofId, pressureMagnitude * surfaceBasisVector3[k] * nurbs.NurbsValues[i, j] * gaussPoints[j].WeightFactor);
						}
					}
				}
			}

			return pressureLoad;
		}

		public void ClearMaterialState()
		{
		}

		public void ClearMaterialStresses() => throw new NotImplementedException();

		public IMatrix DampingMatrix(IElement element) => throw new NotImplementedException();

		public IReadOnlyList<IReadOnlyList<IDofType>> GetElementDofTypes(IElement element)
		{
			dofTypes = new IDofType[element.Nodes.Count][];
			for (var i = 0; i < element.Nodes.Count; i++)
			{
				dofTypes[i] = ControlPointDofTypes;
			}

			return dofTypes;
		}

		public (double[,] MembraneConstitutiveMatrix, double[,] BendingConstitutiveMatrix, double[,]
			CouplingConstitutiveMatrix) IntegratedConstitutiveOverThickness(GaussLegendrePoint3D midSurfaceGaussPoint)
		{
			var MembraneConstitutiveMatrix = new double[3, 3];
			var BendingConstitutiveMatrix = new double[3, 3];
			var CouplingConstitutiveMatrix = new double[3, 3];

			foreach (var keyValuePair in materialsAtThicknessGP[midSurfaceGaussPoint])
			{
				var thicknessPoint = keyValuePair.Key;
				var material = keyValuePair.Value;
				var constitutiveMatrixM = material.ConstitutiveMatrix;
				double tempc = 0;
				double w = thicknessPoint.WeightFactor;
				double z = thicknessPoint.Zeta;
				for (int i = 0; i < 3; i++)
				{
					for (int k = 0; k < 3; k++)
					{
						tempc = constitutiveMatrixM[i, k];
						MembraneConstitutiveMatrix[i, k] += tempc * w;
						CouplingConstitutiveMatrix[i, k] += tempc * w * z;
						BendingConstitutiveMatrix[i, k] += tempc * w * z * z;
					}
				}
			}

			return (MembraneConstitutiveMatrix, BendingConstitutiveMatrix, CouplingConstitutiveMatrix);
		}

		public (double[] MembraneForces, double[] BendingMoments) IntegratedStressesOverThickness(GaussLegendrePoint3D midSurfaceGaussPoint)
		{
			var MembraneForces = new double[3];
			var BendingMoments = new double[3];
			var thicknessPoints = thicknessIntegrationPoints[midSurfaceGaussPoint];

			for (int i = 0; i < thicknessPoints.Count; i++)
			{
				var thicknessPoint = thicknessPoints[i];
				var material = materialsAtThicknessGP[midSurfaceGaussPoint][thicknessPoints[i]];
				var w = thicknessPoint.WeightFactor;
				var z = thicknessPoint.Zeta;
				for (int j = 0; j < 3; j++)
				{
					MembraneForces[j] += material.Stresses[j] * w;
					BendingMoments[j] -= material.Stresses[j] * w * z;
				}
			}

			return (MembraneForces, BendingMoments);
		}

		public IMatrix MassMatrix(IElement element) => throw new NotImplementedException();

		public void ResetMaterialModified() => throw new NotImplementedException();

		public void SaveMaterialState()
		{
			foreach (var gp in materialsAtThicknessGP.Keys)
			{
				foreach (var material in materialsAtThicknessGP[gp].Values)
				{
					material.SaveState();
				}
			}
		}

		public IMatrix StiffnessMatrix(IElement element)
		{
			var shellElement = (NurbsKirchhoffLoveShellElementNL)element;
			var gaussPoints = materialsAtThicknessGP.Keys.ToArray();

			var controlPoints = shellElement.ControlPoints.ToArray();
			var nurbs = new Nurbs2D(shellElement, controlPoints);

			if (!isInitialized)
			{
				CalculateInitialConfigurationData(controlPoints, nurbs, gaussPoints);
				isInitialized = true;
			}

			var elementControlPoints = CurrentControlPoint(controlPoints);

			var bRows = 3;
			var bCols = elementControlPoints.Length * 3;
			var stiffnessMatrix = new double[bCols, bCols];
			var BmTranspose = new double[bCols, bRows];
			var BbTranspose = new double[bCols, bRows];

			var BmTransposeMultStiffness = new double[bCols, bRows];
			var BbTransposeMultStiffness = new double[bCols, bRows];
			var BmbTransposeMultStiffness = new double[bCols, bRows];
			var BbmTransposeMultStiffness = new double[bCols, bRows];

			for (int j = 0; j < gaussPoints.Length; j++)
			{
				var jacobianMatrix = CalculateJacobian(elementControlPoints, nurbs, j);

				var hessianMatrix = CalculateHessian(elementControlPoints, nurbs, j);
				var surfaceBasisVector1 = CalculateSurfaceBasisVector1(jacobianMatrix, 0);

				var surfaceBasisVector2 = CalculateSurfaceBasisVector1(jacobianMatrix, 1);

				var surfaceBasisVector3 = new[]
				{
					surfaceBasisVector1[1] * surfaceBasisVector2[2] - surfaceBasisVector1[2] * surfaceBasisVector2[1],
					surfaceBasisVector1[2] * surfaceBasisVector2[0] - surfaceBasisVector1[0] * surfaceBasisVector2[2],
					surfaceBasisVector1[0] * surfaceBasisVector2[1] - surfaceBasisVector1[1] * surfaceBasisVector2[0],
				};

				double norm = 0;
				for (int i = 0; i < surfaceBasisVector3.Length; i++)
					norm += surfaceBasisVector3[i] * surfaceBasisVector3[i];
				var J1 = Math.Sqrt(norm);

				for (int i = 0; i < surfaceBasisVector3.Length; i++)
					surfaceBasisVector3[i] = surfaceBasisVector3[i] / J1;

				var surfaceBasisVectorDerivative1 = CalculateSurfaceBasisVector1(hessianMatrix, 0);
				var surfaceBasisVectorDerivative2 = CalculateSurfaceBasisVector1(hessianMatrix, 1);
				var surfaceBasisVectorDerivative12 = CalculateSurfaceBasisVector1(hessianMatrix, 2);

				var Bmembrane = CalculateMembraneDeformationMatrix(elementControlPoints, nurbs, j, surfaceBasisVector1,
					surfaceBasisVector2);
				var Bbending = CalculateBendingDeformationMatrix(elementControlPoints, surfaceBasisVector3, nurbs, j, surfaceBasisVector2,
					surfaceBasisVectorDerivative1, surfaceBasisVector1, J1, surfaceBasisVectorDerivative2,
					surfaceBasisVectorDerivative12);

				var (MembraneConstitutiveMatrix, BendingConstitutiveMatrix, CouplingConstitutiveMatrix) =
					IntegratedConstitutiveOverThickness(gaussPoints[j]);

				double wFactor = InitialJ1[j] * gaussPoints[j].WeightFactor;
				double tempb = 0;
				double tempm = 0;
				Array.Clear(BmTranspose, 0, bRows * bCols);
				Array.Clear(BbTranspose, 0, bRows * bCols);
				for (int i = 0; i < bRows; i++)
				{
					for (int k = 0; k < bCols; k++)
					{
						BmTranspose[k, i] = Bmembrane[i, k] * wFactor;
						BbTranspose[k, i] = Bbending[i, k] * wFactor;
					}
				}

				double tempcm = 0;
				double tempcb = 0;
				double tempcc = 0;
				Array.Clear(BmTransposeMultStiffness, 0, bRows * bCols);
				Array.Clear(BbTransposeMultStiffness, 0, bRows * bCols);
				Array.Clear(BmbTransposeMultStiffness, 0, bRows * bCols);
				Array.Clear(BbmTransposeMultStiffness, 0, bRows * bCols);
				for (int i = 0; i < bCols; i++)
				{
					for (int k = 0; k < bRows; k++)
					{
						tempm = BmTranspose[i, k];
						tempb = BbTranspose[i, k];
						for (int m = 0; m < bRows; m++)
						{
							tempcm = MembraneConstitutiveMatrix[k, m];
							tempcb = BendingConstitutiveMatrix[k, m];
							tempcc = CouplingConstitutiveMatrix[k, m];

							BmTransposeMultStiffness[i, m] += tempm * tempcm;
							BbTransposeMultStiffness[i, m] += tempb * tempcb;
							BmbTransposeMultStiffness[i, m] += tempm * tempcc;
							BbmTransposeMultStiffness[i, m] += tempb * tempcc;
						}
					}
				}

				double tempmb = 0;
				double tempbm = 0;
				double mem = 0;
				double ben = 0;
				for (int i = 0; i < bCols; i++)
				{
					for (int k = 0; k < bRows; k++)
					{
						tempm = BmTransposeMultStiffness[i, k];
						tempb = BbTransposeMultStiffness[i, k];
						tempmb = BmbTransposeMultStiffness[i, k];
						tempbm = BbmTransposeMultStiffness[i, k];

						for (int m = 0; m < bCols; m++)
						{
							mem = Bmembrane[k, m];
							ben = Bbending[k, m];
							stiffnessMatrix[i, m] += tempm * mem + tempb * ben + tempmb * ben + tempbm * mem;
						}
					}
				}

				//var (MembraneForces, BendingMoments) = IntegratedStressesOverThickness(gaussPoints[j]);

				//var KmembraneNL = CalculateKmembraneNL(elementControlPoints, MembraneForces, nurbs, j);

				//var KbendingNL = CalculateKbendingNL(elementControlPoints, BendingMoments, nurbs,
				//	Vector.CreateFromArray(surfaceBasisVector1), Vector.CreateFromArray(surfaceBasisVector2),
				//	Vector.CreateFromArray(surfaceBasisVector3),
				//	Vector.CreateFromArray(surfaceBasisVectorDerivative1),
				//	Vector.CreateFromArray(surfaceBasisVectorDerivative2),
				//	Vector.CreateFromArray(surfaceBasisVectorDerivative12), J1, j);

				//for (var i = 0; i < stiffnessMatrix.GetLength(0); i++)
				//{
				//	for (var k = 0; k < stiffnessMatrix.GetLength(1); k++)
				//	{
				//		stiffnessMatrix[i, k] += KmembraneNL[i, k] * wFactor;
				//		stiffnessMatrix[i, k] += KbendingNL[i, k] * wFactor;
				//	}
				//}
			}

			return Matrix.CreateFromArray(stiffnessMatrix);
		}

		private ControlPoint[] CurrentControlPoint(ControlPoint[] controlPoints)
		{
			var cp = new ControlPoint[controlPoints.Length];

			for (int i = 0; i < controlPoints.Length; i++)
			{
				cp[i] = new ControlPoint()
				{
					X = controlPoints[i].X + _solution[i * 3],
					Y = controlPoints[i].Y + _solution[i * 3 + 1],
					Z = controlPoints[i].Z + _solution[i * 3 + 2],
					Ksi = controlPoints[i].Ksi,
					Heta = controlPoints[i].Heta,
					Zeta = controlPoints[i].Zeta,
					WeightFactor = controlPoints[i].WeightFactor
				};
			}

			return cp;
		}

		private ControlPoint[] DControlPoint(ControlPoint[] controlPoints, double[] incrementDisp)
		{
			var cp = new ControlPoint[controlPoints.Length];

			for (int i = 0; i < controlPoints.Length; i++)
			{
				cp[i] = new ControlPoint()
				{
					X = controlPoints[i].X + incrementDisp[i * 3],
					Y = controlPoints[i].Y + incrementDisp[i * 3 + 1],
					Z = controlPoints[i].Z + incrementDisp[i * 3 + 2],
					Ksi = controlPoints[i].Ksi,
					Heta = controlPoints[i].Heta,
					Zeta = controlPoints[i].Zeta,
					WeightFactor = controlPoints[i].WeightFactor
				};
			}

			return cp;
		}


		private static double[,] CalculateHessian(ControlPoint[] controlPoints, Nurbs2D nurbs, int j)
		{
			var hessianMatrix = new double[3, 3];
			for (var k = 0; k < controlPoints.Length; k++)
			{
				hessianMatrix[0, 0] +=
					nurbs.NurbsSecondDerivativeValueKsi[k, j] * controlPoints[k].X;
				hessianMatrix[0, 1] +=
					nurbs.NurbsSecondDerivativeValueKsi[k, j] * controlPoints[k].Y;
				hessianMatrix[0, 2] +=
					nurbs.NurbsSecondDerivativeValueKsi[k, j] * controlPoints[k].Z;
				hessianMatrix[1, 0] +=
					nurbs.NurbsSecondDerivativeValueHeta[k, j] * controlPoints[k].X;
				hessianMatrix[1, 1] +=
					nurbs.NurbsSecondDerivativeValueHeta[k, j] * controlPoints[k].Y;
				hessianMatrix[1, 2] +=
					nurbs.NurbsSecondDerivativeValueHeta[k, j] * controlPoints[k].Z;
				hessianMatrix[2, 0] +=
					nurbs.NurbsSecondDerivativeValueKsiHeta[k, j] * controlPoints[k].X;
				hessianMatrix[2, 1] +=
					nurbs.NurbsSecondDerivativeValueKsiHeta[k, j] * controlPoints[k].Y;
				hessianMatrix[2, 2] +=
					nurbs.NurbsSecondDerivativeValueKsiHeta[k, j] * controlPoints[k].Z;
			}

			return hessianMatrix;
		}

		private static double[,] CalculateJacobian(ControlPoint[] controlPoints, Nurbs2D nurbs, int j)
		{
			var jacobianMatrix = new double[2, 3];
			for (var k = 0; k < controlPoints.Length; k++)
			{
				jacobianMatrix[0, 0] += nurbs.NurbsDerivativeValuesKsi[k, j] * controlPoints[k].X;
				jacobianMatrix[0, 1] += nurbs.NurbsDerivativeValuesKsi[k, j] * controlPoints[k].Y;
				jacobianMatrix[0, 2] += nurbs.NurbsDerivativeValuesKsi[k, j] * controlPoints[k].Z;
				jacobianMatrix[1, 0] += nurbs.NurbsDerivativeValuesHeta[k, j] * controlPoints[k].X;
				jacobianMatrix[1, 1] += nurbs.NurbsDerivativeValuesHeta[k, j] * controlPoints[k].Y;
				jacobianMatrix[1, 2] += nurbs.NurbsDerivativeValuesHeta[k, j] * controlPoints[k].Z;
			}

			return jacobianMatrix;
		}

		private static double[] CalculateSurfaceBasisVector1(double[,] Matrix, int row)
		{
			var surfaceBasisVector1 = new double[3];
			surfaceBasisVector1[0] = Matrix[row, 0];
			surfaceBasisVector1[1] = Matrix[row, 1];
			surfaceBasisVector1[2] = Matrix[row, 2];
			return surfaceBasisVector1;
		}

		private double[,] CalculateA3r(double dKsi, double dHeta,
			double[] surfaceBasisVector2, double[] surfaceBasisVector1)
		{
			var a3r = new double[3, 3];
			a3r[0, 1] = -dKsi * surfaceBasisVector2[2] + surfaceBasisVector1[2] * dHeta;
			a3r[0, 2] = dKsi * surfaceBasisVector2[1] + -surfaceBasisVector1[1] * dHeta;

			a3r[1, 0] = dKsi * surfaceBasisVector2[2] - surfaceBasisVector1[2] * dHeta;
			a3r[1, 2] = -dKsi * surfaceBasisVector2[0] + surfaceBasisVector1[0] * dHeta;

			a3r[2, 0] = -dKsi * surfaceBasisVector2[1] + surfaceBasisVector1[1] * dHeta;
			a3r[2, 1] = dKsi * surfaceBasisVector2[0] + -surfaceBasisVector1[0] * dHeta;
			return a3r;
		}

		private double[,] CalculateBendingDeformationMatrix(ControlPoint[] controlPoints, double[] surfaceBasisVector3,
			Nurbs2D nurbs, int j, double[] surfaceBasisVector2, double[] surfaceBasisVectorDerivative1, double[] surfaceBasisVector1,
			double J1, double[] surfaceBasisVectorDerivative2, double[] surfaceBasisVectorDerivative12)
		{
			var Bbending = new double[3, controlPoints.Length * 3];
			var s1 = Vector.CreateFromArray(surfaceBasisVector1);
			var s2 = Vector.CreateFromArray(surfaceBasisVector2);
			var s3 = Vector.CreateFromArray(surfaceBasisVector3);
			var s11 = Vector.CreateFromArray(surfaceBasisVectorDerivative1);
			var s22 = Vector.CreateFromArray(surfaceBasisVectorDerivative2);
			var s12 = Vector.CreateFromArray(surfaceBasisVectorDerivative12);
			for (int column = 0; column < controlPoints.Length * 3; column += 3)
			{
				#region BI1

				var BI1 = s3.CrossProduct(s1);
				BI1.ScaleIntoThis(nurbs.NurbsDerivativeValuesHeta[column / 3, j]);

				var auxVector = s2.CrossProduct(s3);
				auxVector.ScaleIntoThis(nurbs.NurbsDerivativeValuesKsi[column / 3, j]);
				BI1.AddIntoThis(auxVector);

				BI1.ScaleIntoThis(s3.DotProduct(s11));
				auxVector = s1.CrossProduct(s11);
				auxVector.ScaleIntoThis(nurbs.NurbsDerivativeValuesHeta[column / 3, j]);
				BI1.AddIntoThis(auxVector);

				auxVector = s11.CrossProduct(s2);
				auxVector.ScaleIntoThis(nurbs.NurbsDerivativeValuesKsi[column / 3, j]);
				BI1.AddIntoThis(auxVector);

				BI1.ScaleIntoThis(1 / J1);
				auxVector[0] = surfaceBasisVector3[0];
				auxVector[1] = surfaceBasisVector3[1];
				auxVector[2] = surfaceBasisVector3[2];
				auxVector.ScaleIntoThis(-nurbs.NurbsSecondDerivativeValueKsi[column / 3, j]);
				BI1.AddIntoThis(auxVector);

				#endregion BI1

				#region BI2

				IVector BI2 = s3.CrossProduct(s1);
				BI2.ScaleIntoThis(nurbs.NurbsDerivativeValuesHeta[column / 3, j]);
				auxVector = s2.CrossProduct(s3);
				auxVector.ScaleIntoThis(nurbs.NurbsDerivativeValuesKsi[column / 3, j]);
				BI2.AddIntoThis(auxVector);
				BI2.ScaleIntoThis(s3.DotProduct(s22));
				auxVector = s1.CrossProduct(s22);
				auxVector.ScaleIntoThis(nurbs.NurbsDerivativeValuesHeta[column / 3, j]);
				BI2.AddIntoThis(auxVector);
				auxVector = s22.CrossProduct(s2);
				auxVector.ScaleIntoThis(nurbs.NurbsDerivativeValuesKsi[column / 3, j]);
				BI2.AddIntoThis(auxVector);
				BI2.ScaleIntoThis(1 / J1);
				auxVector[0] = surfaceBasisVector3[0];
				auxVector[1] = surfaceBasisVector3[1];
				auxVector[2] = surfaceBasisVector3[2];
				auxVector.ScaleIntoThis(-nurbs.NurbsSecondDerivativeValueHeta[column / 3, j]);
				BI2.AddIntoThis(auxVector);

				#endregion BI2

				#region BI3

				var BI3 = s3.CrossProduct(s1);
				BI3.ScaleIntoThis(nurbs.NurbsDerivativeValuesHeta[column / 3, j]);
				auxVector = s2.CrossProduct(s3);
				auxVector.ScaleIntoThis(nurbs.NurbsDerivativeValuesKsi[column / 3, j]);
				BI3.AddIntoThis(auxVector);
				BI3.ScaleIntoThis(s3.DotProduct(s12));
				auxVector = s1.CrossProduct(s12);
				auxVector.ScaleIntoThis(nurbs.NurbsDerivativeValuesHeta[column / 3, j]);
				BI3.AddIntoThis(auxVector);
				auxVector = s22.CrossProduct(s2);
				auxVector.ScaleIntoThis(nurbs.NurbsDerivativeValuesKsi[column / 3, j]);
				BI3.AddIntoThis(auxVector);
				BI3.ScaleIntoThis(1 / J1);
				auxVector[0] = surfaceBasisVector3[0];
				auxVector[1] = surfaceBasisVector3[1];
				auxVector[2] = surfaceBasisVector3[2];
				auxVector.ScaleIntoThis(-nurbs.NurbsSecondDerivativeValueKsiHeta[column / 3, j]);
				BI3.AddIntoThis(auxVector);

				#endregion BI3

				//Bbending[0, column] = BI1[0];
				//Bbending[0, column + 1] = BI1[1];
				//Bbending[0, column + 2] = BI1[2];

				//Bbending[1, column] = BI2[0];
				//Bbending[1, column + 1] = BI2[1];
				//Bbending[1, column + 2] = BI2[2];

				//Bbending[2, column] = 2 * BI3[0];
				//Bbending[2, column + 1] = 2 * BI3[1];
				//Bbending[2, column + 2] = 2 * BI3[2];

				Bbending[0, column] = BI1[0];
				Bbending[0, column + 1] = BI1[1];
				Bbending[0, column + 2] = BI1[2];

				Bbending[1, column] = BI2[0];
				Bbending[1, column + 1] = BI2[1];
				Bbending[1, column + 2] = BI2[2];

				Bbending[2, column] = 2 * BI3[0];
				Bbending[2, column + 1] = 2 * BI3[1];
				Bbending[2, column + 2] = 2 * BI3[2];
			}
			return Bbending;
		}

		private double[] CalculateCrossProduct(double[] vector1, double[] vector2)
		{
			return new[]
			{
				vector1[1] * vector2[2] - vector1[2] * vector2[1],
				vector1[2] * vector2[0] - vector1[0] * vector2[2],
				vector1[0] * vector2[1] - vector1[1] * vector2[0]
			};
		}

		private double[][] initialSurfaceBasisVectors1;
		private double[][] initialSurfaceBasisVectors2;
		private double[][] initialUnitSurfaceBasisVectors3;

		private double[][] initialSurfaceBasisVectorDerivative1;
		private double[][] initialSurfaceBasisVectorDerivative2;
		private double[][] initialSurfaceBasisVectorDerivative12;

		private double[] InitialJ1;

		private void CalculateInitialConfigurationData(ControlPoint[] controlPoints,
			Nurbs2D nurbs, IList<GaussLegendrePoint3D> gaussPoints)
		{
			var numberOfGP = gaussPoints.Count;
			InitialJ1=new double[numberOfGP];
			initialSurfaceBasisVectors1 = new double[numberOfGP][];
			initialSurfaceBasisVectors2 = new double[numberOfGP][];
			initialUnitSurfaceBasisVectors3 = new double[numberOfGP][];
			initialSurfaceBasisVectorDerivative1 = new double[numberOfGP][];
			initialSurfaceBasisVectorDerivative2 = new double[numberOfGP][];
			initialSurfaceBasisVectorDerivative12 = new double[numberOfGP][];

			for (int j = 0; j < gaussPoints.Count; j++)
			{
				var jacobianMatrix = CalculateJacobian(controlPoints, nurbs, j);

				var hessianMatrix = CalculateHessian(controlPoints, nurbs, j);
				initialSurfaceBasisVectors1[j] = CalculateSurfaceBasisVector1(jacobianMatrix, 0);
				initialSurfaceBasisVectors2[j] = CalculateSurfaceBasisVector1(jacobianMatrix, 1);
				var s3= CalculateCrossProduct(initialSurfaceBasisVectors1[j], initialSurfaceBasisVectors2[j]);
				var norm = s3.Sum(t => t * t);
				InitialJ1[j] = Math.Sqrt(norm);
				 var vector3= CalculateCrossProduct(initialSurfaceBasisVectors1[j], initialSurfaceBasisVectors2[j]);
				 initialUnitSurfaceBasisVectors3[j]= new double[]
				 {
					 vector3[0]/InitialJ1[j],
					 vector3[1]/InitialJ1[j],
					 vector3[2]/InitialJ1[j],
				 };

				initialSurfaceBasisVectorDerivative1[j] = CalculateSurfaceBasisVector1(hessianMatrix, 0);
				initialSurfaceBasisVectorDerivative2[j] = CalculateSurfaceBasisVector1(hessianMatrix, 1);
				initialSurfaceBasisVectorDerivative12[j] = CalculateSurfaceBasisVector1(hessianMatrix, 2);

				foreach (var integrationPointMaterial in materialsAtThicknessGP[gaussPoints[j]].Values)
				{
					integrationPointMaterial.TangentVectorV1 = initialSurfaceBasisVectors1[j];
					integrationPointMaterial.TangentVectorV2 = initialSurfaceBasisVectors2[j];
					integrationPointMaterial.NormalVectorV3 = initialUnitSurfaceBasisVectors3[j];
				}
			}
		}

		private IMatrix CalculateKbendingNL(ControlPoint[] controlPoints,
		   double[] bendingMoments, Nurbs2D nurbs, Vector surfaceBasisVector1,
		   Vector surfaceBasisVector2, Vector surfaceBasisVector3, Vector surfaceBasisVectorDerivative1, Vector surfaceBasisVectorDerivative2,
		   Vector surfaceBasisVectorDerivative12, double J1, int j)
		{
			var KbendingNL =
				Matrix.CreateZero(controlPoints.Length * 3, controlPoints.Length * 3);

			for (int i = 0; i < controlPoints.Length; i++)
			{
				var a1r = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsDerivativeValuesKsi[i, j]);
				var a2r = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsDerivativeValuesHeta[i, j]);

				var a11r = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsSecondDerivativeValueKsi[i, j]);
				var a22r = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsSecondDerivativeValueHeta[i, j]);
				var a12r = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsSecondDerivativeValueKsiHeta[i, j]);
				for (int k = 0; k < controlPoints.Length; k++)
				{
					var a11s = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsSecondDerivativeValueKsi[k, j]);
					var a22s = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsSecondDerivativeValueHeta[k, j]);
					var a12s = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsSecondDerivativeValueKsiHeta[k, j]);

					var a3r = CalculateA3r(nurbs, i, j, surfaceBasisVector2, surfaceBasisVector1);
					var a3s = CalculateA3r(nurbs, k, j, surfaceBasisVector2, surfaceBasisVector1);

					var a1s = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsDerivativeValuesKsi[k, j]);
					var a2s = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsDerivativeValuesHeta[k, j]);

					#region B

					var term1_532 = new Vector[3, 3];
					for (int m = 0; m < 3; m++)
					{
						for (int n = 0; n < 3; n++)
						{
							var temp = a1r.GetRow(m).CrossProduct(a2s.GetRow(n));
							temp.ScaleIntoThis(J1);
							term1_532[m, n] = temp;
						}
					}

					var term2_532 = new Vector[3, 3];
					for (int m = 0; m < 3; m++)
					{
						var a3r_dashed = a1r.GetRow(m).CrossProduct(surfaceBasisVector2) +
										 surfaceBasisVector1.CrossProduct(a2r.GetRow(m));
						for (int n = 0; n < 3; n++)
						{
							//TODO: a3s_dashed, a3r_dashed calculated out of the loop for all cp
							var a3s_dashed = a1s.GetRow(n).CrossProduct(surfaceBasisVector2) +
											 surfaceBasisVector1.CrossProduct(a2s.GetRow(n));
							var term_525 = surfaceBasisVector3 * a3s_dashed;
							term2_532[m, n] = a3r_dashed.Scale(-term_525 / J1 / J1);
						}
					}

					var term3_532 = new Vector[3, 3];
					for (int m = 0; m < 3; m++)
					{
						var a3r_dashed = a1r.GetRow(m).CrossProduct(surfaceBasisVector2) +
										 surfaceBasisVector1.CrossProduct(a2r.GetRow(m));
						for (int n = 0; n < 3; n++)
						{
							var a3s_dashed = a1s.GetRow(n).CrossProduct(surfaceBasisVector2) +
											 surfaceBasisVector1.CrossProduct(a2s.GetRow(n));
							var term_525 = surfaceBasisVector3 * a3r_dashed;
							term3_532[m, n] = a3s_dashed.Scale(-term_525 / J1 / J1);
						}
					}

					var term4_532 = new Vector[3, 3];
					for (int m = 0; m < 3; m++)
					{
						var a3r_dashed = a1r.GetRow(m).CrossProduct(surfaceBasisVector2) +
										 surfaceBasisVector1.CrossProduct(a2r.GetRow(m));
						for (int n = 0; n < 3; n++)
						{
							var a3s_dashed = a1s.GetRow(n).CrossProduct(surfaceBasisVector2) +
											 surfaceBasisVector1.CrossProduct(a2s.GetRow(n));
							// term 5_31
							var a3_rs = term1_532[m, n] * surfaceBasisVector3 * J1 + a3r_dashed * a3s_dashed / J1 -
										(a3r_dashed * surfaceBasisVector3) * (a3s_dashed * surfaceBasisVector3) / J1;
							term4_532[m, n] = surfaceBasisVector3.Scale(-a3_rs / J1);
						}
					}

					var term5_532 = new Vector[3, 3];
					for (int m = 0; m < 3; m++)
					{
						var a3r_dashed = a1r.GetRow(m).CrossProduct(surfaceBasisVector2) +
										 surfaceBasisVector1.CrossProduct(a2r.GetRow(m));
						var term_525_r = surfaceBasisVector3 * a3r_dashed;
						for (int n = 0; n < 3; n++)
						{
							var a3s_dashed = a1s.GetRow(n).CrossProduct(surfaceBasisVector2) +
											 surfaceBasisVector1.CrossProduct(a2s.GetRow(n));
							var term_525_s = surfaceBasisVector3 * a3s_dashed;
							term5_532[m, n] = surfaceBasisVector3.Scale(2 / J1 / J1 * term_525_r * term_525_s);
						}
					}

					var a3rs = new Vector[3, 3];
					for (int m = 0; m < 3; m++)
					{
						for (int n = 0; n < 3; n++)
						{
							a3rs[m, n] = term1_532[m, n] + term2_532[m, n] + term3_532[m, n] + term4_532[m, n] +
										 term5_532[m, n];
						}
					}

					#endregion B

					var termA = bendingMoments[0] * (a11r * a3s + a11s * a3r) +
								bendingMoments[1] * (a22r * a3s + a22s * a3r) +
								bendingMoments[2] * (a12r * a3s + a12s * a3r) * 2;

					var aux = bendingMoments[0] * surfaceBasisVectorDerivative1 +
							   bendingMoments[1] * surfaceBasisVectorDerivative2 +
							   2 * bendingMoments[2] * surfaceBasisVectorDerivative12;
					var termB = Matrix3by3.CreateZero();
					for (int m = 0; m < 3; m++)
					{
						for (int n = 0; n < 3; n++)
						{
							termB[m, n] = aux * a3rs[m, n];
						}
					}

					var gaussPointStiffness = termA + termB;

					for (int l = 0; l < 3; l++)
					{
						for (int m = 0; m < 3; m++)
						{
							KbendingNL[i * 3 + l, k * 3 + m] += gaussPointStiffness[l, m];
						}
					}
				}
			}

			return KbendingNL;
		}


		private Matrix3by3 CalculateA3r(Nurbs2D nurbs, int i, int j,
			Vector surfaceBasisVector2, Vector surfaceBasisVector1)
		{
			var aux1 = Vector.CreateFromArray(new double[] { nurbs.NurbsDerivativeValuesKsi[i, j], 0, 0 })
				.CrossProduct(surfaceBasisVector2);
			var aux2 = Vector.CreateFromArray(new double[] { 0, nurbs.NurbsDerivativeValuesKsi[i, j], 0 })
				.CrossProduct(surfaceBasisVector2);
			var aux3 = Vector.CreateFromArray(new double[] { 0, 0, nurbs.NurbsDerivativeValuesKsi[i, j] })
				.CrossProduct(surfaceBasisVector2);

			var aux4 = surfaceBasisVector1.CrossProduct(Vector.CreateFromArray(new double[]
				{nurbs.NurbsDerivativeValuesHeta[i, j], 0, 0}));
			var aux5 = surfaceBasisVector1.CrossProduct(Vector.CreateFromArray(new double[]
				{0, nurbs.NurbsDerivativeValuesHeta[i, j], 0}));
			var aux6 = surfaceBasisVector1.CrossProduct(Vector.CreateFromArray(new double[]
				{0, 0, nurbs.NurbsDerivativeValuesHeta[i, j]}));

			var a3r = Matrix3by3.CreateZero();
			a3r[0, 0] = aux1[0] + aux4[0];
			a3r[0, 0] = aux1[1] + aux4[1];
			a3r[0, 0] = aux1[2] + aux4[2];

			a3r[0, 0] = aux2[0] + aux5[0];
			a3r[0, 0] = aux2[1] + aux5[1];
			a3r[0, 0] = aux2[2] + aux5[2];

			a3r[0, 0] = aux3[0] + aux6[0];
			a3r[0, 0] = aux3[1] + aux6[1];
			a3r[0, 0] = aux3[2] + aux6[2];
			return a3r;
		}

		private Matrix CalculateKmembraneNL(ControlPoint[] controlPoints, double[] membraneForces, Nurbs2D nurbs, int j)
		{
			var kmembraneNl =
				Matrix.CreateZero(controlPoints.Length * 3, controlPoints.Length * 3);

			for (var i = 0; i < controlPoints.Length; i++)
			{
				var a1r = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsDerivativeValuesKsi[i, j]);
				var a2r = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsDerivativeValuesHeta[i, j]);
				for (int k = 0; k < controlPoints.Length; k++)
				{
					var a1s = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsDerivativeValuesKsi[k, j]);
					var a2s = Matrix3by3.CreateIdentity().Scale(nurbs.NurbsDerivativeValuesHeta[k, j]);

					var klocal = membraneForces[0] * a1r * a1s + membraneForces[1] * a2r * a2s +
								 membraneForces[2] * (a1r * a2s + a1s * a2r);

					for (int l = 0; l < 3; l++)
					{
						for (int m = 0; m < 3; m++)
						{
							kmembraneNl[i * 3 + l, k * 3 + m] += klocal[l, m];
						}
					}
				}
			}

			return kmembraneNl;
		}

		private double[,] CreateDiagonal3by3WithValue(double value)
		{
			var matrix = new double[3, 3];
			matrix[0, 0] = value;
			matrix[1, 1] = value;
			matrix[2, 2] = value;
			return matrix;
		}

		
		private double[,] CalculateMembraneDeformationMatrix(ControlPoint[] controlPoints, Nurbs2D nurbs, int j,
			double[] surfaceBasisVector1,
			double[] surfaceBasisVector2)
		{
			var dRIa = new double[3, controlPoints.Length * 3];
			for (int i = 0; i < controlPoints.Length; i++)
			{
				for (int m = 0; m < 3; m++)
				{
					dRIa[m, i] = nurbs.NurbsDerivativeValuesHeta[i, j] * surfaceBasisVector1[m] +
								 nurbs.NurbsDerivativeValuesKsi[i, j] * surfaceBasisVector2[m];
				}
			}

			var bmembrane = new double[3, controlPoints.Length * 3];
			for (int column = 0; column < controlPoints.Length * 3; column += 3)
			{
				bmembrane[0, column] = nurbs.NurbsDerivativeValuesKsi[column / 3, j] * surfaceBasisVector1[0];
				bmembrane[0, column + 1] = nurbs.NurbsDerivativeValuesKsi[column / 3, j] * surfaceBasisVector1[1];
				bmembrane[0, column + 2] = nurbs.NurbsDerivativeValuesKsi[column / 3, j] * surfaceBasisVector1[2];

				bmembrane[1, column] = nurbs.NurbsDerivativeValuesHeta[column / 3, j] * surfaceBasisVector2[0];
				bmembrane[1, column + 1] = nurbs.NurbsDerivativeValuesHeta[column / 3, j] * surfaceBasisVector2[1];
				bmembrane[1, column + 2] = nurbs.NurbsDerivativeValuesHeta[column / 3, j] * surfaceBasisVector2[2];

				bmembrane[2, column] = dRIa[0, column / 3];
				bmembrane[2, column + 1] = dRIa[1, column / 3];
				bmembrane[2, column + 2] = dRIa[2, column / 3];
			}

			return bmembrane;
		}

		private double[,] CopyConstitutiveMatrix(double[,] f)
		{
			var g = new double[f.GetLength(0), f.GetLength(1)];
			Array.Copy(f, 0, g, 0, f.Length);
			return g;
		}

		private IList<GaussLegendrePoint3D> CreateElementGaussPoints(NurbsKirchhoffLoveShellElementNL shellElement)
		{
			var gauss = new GaussQuadrature();
			//var medianSurfaceGP = gauss.CalculateElementGaussPoints(shellElement.Patch.DegreeKsi, shellElement.Patch.DegreeHeta, shellElement.Knots.ToList());
			var medianSurfaceGP = gauss.CalculateElementGaussPoints(shellElement.Patch.DegreeKsi, shellElement.Patch.DegreeHeta, shellElement.Knots.ToList());
			foreach (var point in medianSurfaceGP)
			{
				var gp = gauss.CalculateElementGaussPoints(ThicknessIntegrationDegree,
					new List<Knot>
					{
						new Knot() {ID = 0, Ksi = -shellElement.Thickness / 2, Heta = point.Heta},
						new Knot() {ID = 1, Ksi = shellElement.Thickness / 2, Heta = point.Heta},
					}).ToList();

				thicknessIntegrationPoints.Add(point,
					gp.Select(g => new GaussLegendrePoint3D(point.Ksi, point.Heta, g.Ksi, g.WeightFactor))
						.ToList());
			}

			return medianSurfaceGP;
		}

		private const int ThicknessIntegrationDegree = 2;

		public double Thickness { get; set; }
	}
}
